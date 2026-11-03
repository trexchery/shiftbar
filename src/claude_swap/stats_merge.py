"""``cswap stats-sync``: count every profile's Claude Code usage in the default login's ``/stats``.

``/stats`` ("All time") reads ``~/.claude/stats-cache.json`` (finished UTC days, frozen once computed)
plus a live scan of today's transcripts in ``~/.claude/projects``. Accounts that run through
``cswap run`` (terminals, Auto-Prime, reopened windows) write their transcripts to
``<backup>/sessions/<n>-<email>/projects`` instead, so the default ``/stats`` never sees them.

This replays Claude Code's own aggregation (2.1.316, cache format 5) over those transcripts and adds
the result to the default cache, one finished day at a time. Transcripts, logins and running sessions
are never touched; only the cache Claude itself treats as derived data is rewritten, atomically.

- Accuracy: Claude's entry filter, UTC-day and local-hour buckets, 100 MiB tail read and token sums.
  A transcript copied between profiles (a window reopened on another account) counts once, by entry
  uuid. Days older than a profile's transcripts come from that profile's own stats cache.
- Watermark: side usage for days the default cache has not reached yet goes in together with the
  default's own days, computed the same way, so ``lastComputedDate`` moves as Claude would move it.
- Ledger (``<backup>/stats-merge.json``): every day added and the cache entries it produced. A day
  whose entries no longer match (a ``/stats`` write that crossed ours, a wiped cache) is added again;
  a matching one never is. Written before the cache, so a crash between the two only repeats work.
- Any other cache format is left alone.

Not covered: side usage from today (it lands once the UTC day is over) and the 7d/30d tabs, which
Claude computes from ``~/.claude/projects`` alone.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import logging
import math
import os
import shutil
import sys
import tempfile
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Callable

from claude_swap.fsutil import replace_with_retry
from claude_swap.locking import FileLock
from claude_swap.paths import get_backup_root, get_default_claude_config_home
from claude_swap.settings import atomic_write_json

log = logging.getLogger(__name__)

FORMAT = 5  # the stats-cache.json `version` and `dailyModelTokensVersion` this replica matches
TAIL = 100 * 1024 * 1024  # Claude reads a bigger transcript from its last 100 MiB only
TYPES = frozenset(("user", "assistant", "attachment", "system"))
SYNTHETIC = "<synthetic>"
# Claude skips a model named like an Object.prototype member.
PROTO = frozenset((
    "constructor", "hasOwnProperty", "isPrototypeOf", "propertyIsEnumerable", "toLocaleString",
    "toString", "valueOf", "__proto__", "__defineGetter__", "__defineSetter__", "__lookupGetter__",
    "__lookupSetter__",
))
LEDGER = "stats-merge.json"
BEFORE = "stats-cache.pre-merge.json"  # the default cache as it was before the first merge
_SUB = f"{os.sep}subagents{os.sep}"
_EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)
_USAGE = ("input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens")
_MU = ("inputTokens", "outputTokens", "cacheReadInputTokens", "cacheCreationInputTokens")


class LedgerError(Exception):
    """The ledger is unreadable: merging without it would add every day twice."""


# -- Claude's reading of a transcript ------------------------------------------


def _truthy(v) -> bool:
    """JavaScript truthiness of a JSON value."""
    return not (v is None or v is False or v == 0 or v == "")


def _num(v) -> int | float:
    return v if isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) else 0


def _ms(ts) -> int | None:
    """Epoch milliseconds of a timestamp as ``new Date(ts)`` reads it; None when invalid."""
    if isinstance(ts, str):
        try:
            dt = datetime.fromisoformat(ts)
        except ValueError:
            return None
        if dt.tzinfo is None:
            dt = dt.astimezone()  # JS reads a date-time without an offset as local time
        return (dt - _EPOCH) // timedelta(milliseconds=1)
    if isinstance(ts, (int, float)) and not isinstance(ts, bool) and math.isfinite(ts):
        return int(ts)
    return None


_DAYS: dict[int, str] = {}


def _day(ms: int) -> str:
    """The UTC date of ``ms`` (``toISOString().split("T")[0]``)."""
    n = ms // 86_400_000
    d = _DAYS.get(n)
    if d is None:
        d = _DAYS[n] = (_EPOCH + timedelta(days=n)).date().isoformat()
    return d


def _hour(ms: int) -> str:
    """Local hour of ``ms`` (``getHours()``), as hourCounts keys it."""
    try:
        return str(datetime.fromtimestamp(ms / 1000).hour)
    except (OSError, OverflowError, ValueError):
        return "0"


def _mtime_day(st: os.stat_result) -> str:
    return datetime.fromtimestamp(st.st_mtime, timezone.utc).date().isoformat()


def _next(d: str | None) -> str | None:
    return None if d is None else (date.fromisoformat(d) + timedelta(days=1)).isoformat()


def yesterday(now: datetime | None = None) -> str:
    """Claude's last cacheable day: one day back on the local calendar, as a UTC date."""
    return ((now or datetime.now()) - timedelta(days=1)).astimezone(timezone.utc).date().isoformat()


def _reject(name: str):
    raise ValueError(name)  # JSON.parse has no NaN/Infinity


def _read(path: Path) -> list[dict] | None:
    """The transcript's entries ``/stats`` looks at, read as Claude reads them; None if unreadable."""
    try:
        with open(path, "rb") as f:
            size = os.fstat(f.fileno()).st_size
            if size > TAIL:
                f.seek(size - TAIL)
                data = f.read(TAIL)
                cut = data.find(b"\n")
                if cut != -1 and cut < len(data) - 1:
                    data = data[cut + 1:]
            else:
                data = f.read()
    except OSError as e:
        log.warning("stats-sync: cannot read %s: %s", path, e)
        return None
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    out = []
    for line in data.decode("utf-8", "replace").split("\n"):
        line = line.strip()
        if not line:
            continue
        try:
            e = json.loads(line, parse_constant=_reject)
        except (ValueError, RecursionError):
            continue
        if isinstance(e, dict) and isinstance(t := e.get("type"), str) and t in TYPES:
            out.append(e)
    return out


def transcripts(projects: Path) -> list[tuple[Path, str]]:
    """``(path, key)`` for every transcript ``/stats`` reads, in the order Claude lists them.

    ``key`` names a transcript the same way in every profile (``<session>.jsonl`` or
    ``<session>/subagents/agent-<id>.jsonl``), so a copy carried to another profile shares it.
    """

    def entries(d) -> list[os.DirEntry]:
        try:
            with os.scandir(d) as it:
                return list(it)
        except OSError:
            return []

    out = []
    for project in entries(projects):
        if not project.is_dir(follow_symlinks=False):
            continue
        children = entries(project.path)
        out += [(Path(c.path), c.name) for c in children
                if c.is_file(follow_symlinks=False) and c.name.endswith(".jsonl")]
        for c in children:
            if c.is_dir(follow_symlinks=False):
                out += [(Path(a.path), f"{c.name}/subagents/{a.name}")
                        for a in entries(os.path.join(c.path, "subagents"))
                        if a.is_file(follow_symlinks=False) and a.name.endswith(".jsonl")
                        and a.name.startswith("agent-")]
    return out


def _blank() -> dict:
    """One day's contribution: dailyActivity (when ``active``), dailyModelTokens, and its share of
    the cache's running totals (modelUsage, hourCounts, longest and first session)."""
    return {"active": False, "messages": 0, "sessions": 0, "tools": 0, "tokens": {}, "usage": {},
            "hours": {}, "longest": None, "first": None}


class Scan:
    """Claude's ``io()`` for one profile: per-day sums over ``start``..``end`` (UTC, inclusive, None open)."""

    def __init__(self, start: str | None, end: str | None):
        self.start, self.end = start, end
        self.days: dict[str, dict] = {}

    def _get(self, d: str) -> dict:
        x = self.days.get(d)
        if x is None:
            x = self.days[d] = _blank()
        return x

    def wants(self, path: Path) -> bool:
        """Claude's pre-filter: a file last written before the window holds nothing in it."""
        try:
            return not self.start or _mtime_day(path.stat()) >= self.start
        except OSError:
            return True

    def add(self, path: Path, dup: Callable[[object, str], bool] | None = None,
            session: Callable[[str], bool] | None = None) -> None:
        """Count one transcript. ``dup(uuid, day)``: the entry is counted elsewhere already;
        ``session(day)``: whether this copy counts the session itself."""
        if not self.wants(path):
            return
        entries = _read(path)
        if not entries:
            return
        sub = _SUB in str(path)
        kept = entries if sub else [e for e in entries if not _truthy(e.get("isSidechain"))]
        if not kept:
            return
        t0, t1 = _ms(kept[0].get("timestamp")), _ms(kept[-1].get("timestamp"))
        if t0 is None or t1 is None:
            return
        d0 = _day(t0)
        if self.end and self.end < d0:
            return
        if not sub and not (self.start and d0 < self.start) and (session is None or session(d0)):
            x = self._get(d0)
            x["active"] = True
            x["sessions"] += 1
            h = _hour(t0)
            x["hours"][h] = x["hours"].get(h, 0) + 1
            ts = kept[0]["timestamp"]
            rec = {"sessionId": path.name[:-len(".jsonl")], "duration": t1 - t0,
                   "messageCount": len(kept), "timestamp": ts}
            if x["longest"] is None or rec["duration"] > x["longest"]["duration"]:
                x["longest"] = rec
            if isinstance(ts, str) and (x["first"] is None or ts < x["first"]):
                x["first"] = ts
        for e in kept:
            t = _ms(e.get("timestamp"))
            if t is None:
                continue
            d = _day(t)
            if (self.start and d < self.start) or (self.end and d > self.end):
                continue
            if dup is not None and dup(e.get("uuid"), d):
                continue
            x = self.days.get(d)
            if not sub:
                x = self._get(d)
                x["active"] = True
                x["messages"] += 1
            if e["type"] != "assistant" or not isinstance(msg := e.get("message"), dict):
                continue
            content = msg.get("content")
            if isinstance(content, list) and x is not None and x["active"]:
                x["tools"] += sum(1 for b in content if isinstance(b, dict) and b.get("type") == "tool_use")
            usage = msg.get("usage")
            if not _truthy(usage):
                continue
            model = msg.get("model") if _truthy(msg.get("model")) else "unknown"
            if not isinstance(model, str) or model == SYNTHETIC or model in PROTO:
                continue
            nums = [_num(usage.get(k)) for k in _USAGE] if isinstance(usage, dict) else [0, 0, 0, 0]
            x = self._get(d)
            u = x["usage"].setdefault(model, [0, 0, 0, 0])
            for i, n in enumerate(nums):
                u[i] += n
            if (total := sum(nums)) > 0:
                x["tokens"][model] = x["tokens"].get(model, 0) + total


def _sum(into: dict[str, dict], days: dict[str, dict]) -> None:
    for d, x in days.items():
        y = into.setdefault(d, _blank())
        y["active"] |= x["active"]
        for k in ("messages", "sessions", "tools"):
            y[k] += x[k]
        for m, n in x["tokens"].items():
            y["tokens"][m] = y["tokens"].get(m, 0) + n
        for m, u in x["usage"].items():
            y["usage"][m] = [a + b for a, b in zip(y["usage"].get(m, [0, 0, 0, 0]), u)]
        for h, n in x["hours"].items():
            y["hours"][h] = y["hours"].get(h, 0) + n
        if x["longest"] and (not y["longest"] or x["longest"]["duration"] > y["longest"]["duration"]):
            y["longest"] = x["longest"]
        if x["first"] and (not y["first"] or x["first"] < y["first"]):
            y["first"] = x["first"]


def _counts(x: dict) -> bool:
    return bool(x["messages"] or x["sessions"] or x["tools"] or x["tokens"])


# -- the stats cache -------------------------------------------------------------


def _empty() -> dict:
    return {"version": FORMAT, "lastComputedDate": None, "dailyActivity": [], "dailyModelTokens": [],
            "dailyModelTokensVersion": FORMAT, "modelUsage": {}, "totalSessions": 0, "totalMessages": 0,
            "longestSession": None, "firstSessionDate": None, "hourCounts": {}}


def _is_day(v) -> bool:
    try:
        return isinstance(v, str) and len(v) == 10 and date.fromisoformat(v).isoformat() == v
    except ValueError:
        return False


def _valid(c) -> bool:
    return (isinstance(c, dict) and c.get("version") == FORMAT and c.get("dailyModelTokensVersion") == FORMAT
            and isinstance(c.get("dailyActivity"), list) and isinstance(c.get("dailyModelTokens"), list)
            and all(isinstance(e, dict) and _is_day(e.get("date")) for e in c["dailyActivity"])
            and all(isinstance(e, dict) and _is_day(e.get("date")) and isinstance(e.get("tokensByModel"), dict)
                    for e in c["dailyModelTokens"])
            and isinstance(c.get("modelUsage", {}), dict) and isinstance(c.get("hourCounts", {}), dict)
            and all(isinstance(c.get(k), (int, float)) and not isinstance(c.get(k), bool)
                    for k in ("totalSessions", "totalMessages"))
            and (c.get("lastComputedDate") is None or _is_day(c["lastComputedDate"])))


def _watermark(c: dict) -> str | None:
    """``lastComputedDate``, or as Claude fills in a missing one: the newest cached day."""
    days = [e["date"] for e in c["dailyActivity"]] + [e["date"] for e in c["dailyModelTokens"]]
    return c.get("lastComputedDate") or max(days, default=None)


def _load_cache(path: Path) -> tuple[bytes | None, dict | None, str | None]:
    """``(raw, cache, problem)``; a missing cache reads as Claude's empty one."""
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return None, _empty(), None
    except OSError as e:
        return None, None, f"cannot read {path}: {e}"
    try:
        cache = json.loads(raw)
    except ValueError:
        return raw, None, f"{path} is not valid JSON (Claude rebuilds it on the next /stats)"
    if not _valid(cache):
        v = cache.get("version") if isinstance(cache, dict) else None
        return raw, None, f"{path} is format {v!r}, not {FORMAT}: left alone"
    return raw, cache, None


def _apply(cache: dict, days: dict[str, dict]) -> None:
    """Add per-day contributions to ``cache`` the way Claude's own merge adds a new run."""
    activity = {e["date"]: e for e in cache["dailyActivity"]}
    tokens = {e["date"]: e["tokensByModel"] for e in cache["dailyModelTokens"]}
    usage = cache.setdefault("modelUsage", {})
    hours = cache.setdefault("hourCounts", {})
    for d, x in days.items():
        if x["active"]:
            a = activity.setdefault(d, {"date": d, "messageCount": 0, "sessionCount": 0, "toolCallCount": 0})
            for k, n in (("messageCount", x["messages"]), ("sessionCount", x["sessions"]), ("toolCallCount", x["tools"])):
                a[k] = _num(a.get(k)) + n
        if x["tokens"]:
            t = tokens.setdefault(d, {})
            for m, n in x["tokens"].items():
                t[m] = _num(t.get(m)) + n
        for m, u in x["usage"].items():
            mu = usage.get(m)
            if not isinstance(mu, dict):
                mu = usage[m] = {"inputTokens": 0, "outputTokens": 0, "cacheReadInputTokens": 0,
                                 "cacheCreationInputTokens": 0, "webSearchRequests": 0, "costUSD": 0,
                                 "contextWindow": 0, "maxOutputTokens": 0}
            for k, n in zip(_MU, u):
                mu[k] = _num(mu.get(k)) + n
        for h, n in x["hours"].items():
            hours[h] = _num(hours.get(h)) + n
        cache["totalSessions"] += x["sessions"]
        cache["totalMessages"] += x["messages"]
        best = cache.get("longestSession")
        if x["longest"] and not (isinstance(best, dict) and _num(best.get("duration")) >= x["longest"]["duration"]):
            cache["longestSession"] = x["longest"]
        first = cache.get("firstSessionDate")
        if x["first"] and not (isinstance(first, str) and first <= x["first"]):
            cache["firstSessionDate"] = x["first"]
    cache["dailyActivity"] = [activity[d] for d in sorted(activity)]
    cache["dailyModelTokens"] = [{"date": d, "tokensByModel": tokens[d]} for d in sorted(tokens)]


def _entries_of(cache: dict) -> Callable[[str], list]:
    """The cache's own entries for a day, ``[dailyActivity entry, tokensByModel]`` (None when absent)."""
    activity = {e["date"]: e for e in cache["dailyActivity"]}
    tokens = {e["date"]: e["tokensByModel"] for e in cache["dailyModelTokens"]}
    return lambda d: [activity.get(d), tokens.get(d)]


def _write_cache(path: Path, cache: dict) -> None:
    """As Claude writes it: a 0600 temp file renamed over the cache."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=".stats-cache-", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(cache, f, indent=2, ensure_ascii=False)
        replace_with_retry(tmp, path)
    except BaseException:
        with contextlib.suppress(OSError):
            os.unlink(tmp)
        raise


# -- the profiles ----------------------------------------------------------------


def _real(p: Path) -> str:
    return os.path.normcase(os.path.realpath(p))


def profiles(home: Path, store: Path) -> list[Path]:
    """Every ``cswap run`` profile once, without one whose ``projects`` is the default's (a link)."""
    seen, out = {_real(home / "projects")}, []
    for p in sorted((store / "sessions").glob("*")):
        if p.is_dir() and (r := _real(p / "projects")) not in seen:
            seen.add(r)
            out.append(p)
    return out


def _own_cache(profile: Path, files: list[tuple[Path, str]], start: str | None) -> tuple[dict[str, dict], str | None]:
    """Days only the profile's own stats cache still has (its transcripts were cleaned up), and the
    first day its transcripts supply instead."""
    _, cache, _ = _load_cache(profile / "stats-cache.json")
    if cache is None or not (c := _watermark(cache)):
        return {}, start
    oldest = []
    for p, _ in files:
        with contextlib.suppress(OSError):
            oldest.append(_mtime_day(p.stat()))
    # Every transcript touched since the oldest one left survived each cleanup, so the files are
    # complete from that day on; the cache holds what came before, up to its watermark.
    first = min(min(oldest, default=_next(c)), _next(c))
    activity = {e["date"]: e for e in cache["dailyActivity"]}
    tokens = {e["date"]: e["tokensByModel"] for e in cache["dailyModelTokens"]}
    used = sorted(d for d in activity.keys() | tokens.keys() if (start is None or d >= start) and d < first)
    later = first if start is None or first > start else start
    if not used:
        return {}, later
    days = {}
    for d in used:
        x = days[d] = _blank()
        if a := activity.get(d):
            x.update(active=True, messages=_num(a.get("messageCount")), sessions=_num(a.get("sessionCount")),
                     tools=_num(a.get("toolCallCount")))
        x["tokens"] = {m: _num(n) for m, n in tokens.get(d, {}).items() if _num(n)}
    # ponytail: the cache keeps modelUsage and hourCounts as totals only, so the used days get them
    # pro rata (tokens per model, sessions); exact when every cached day is used.
    anchor = days[used[-1]]
    all_tokens, used_tokens = defaultdict(int), defaultdict(int)
    for d, t in tokens.items():
        for m, n in t.items():
            all_tokens[m] += _num(n)
            if d in days:
                used_tokens[m] += _num(n)
    for m, mu in cache.get("modelUsage", {}).items():
        if isinstance(mu, dict) and all_tokens.get(m) and (share := used_tokens[m] / all_tokens[m]):
            anchor["usage"][m] = [round(_num(mu.get(k)) * share) for k in _MU]
    sessions = sum(days[d]["sessions"] for d in used)
    share = sessions / cache["totalSessions"] if cache["totalSessions"] else 0
    anchor["hours"] = {h: n for h, v in cache.get("hourCounts", {}).items() if (n := round(_num(v) * share))}
    ls, fs = cache.get("longestSession"), cache.get("firstSessionDate")
    if isinstance(ls, dict) and (t := _ms(ls.get("timestamp"))) is not None and _day(t) < first:
        anchor["longest"] = ls
    if isinstance(fs, str) and (t := _ms(fs)) is not None and _day(t) < first:
        anchor["first"] = fs
    return days, later


def side_days(home: Path, store: Path, start: str | None, end: str, watermark: str | None,
              cache_mtime: float | None) -> tuple[dict[str, dict], int]:
    """What the profiles add for ``start``..``end``, each copied transcript counted once; and how many
    profiles were read.

    A copy shared with the default profile was (or is now being) counted there: its entries are left
    out here, unless the copy landed after Claude cached that day (born after the cache was written).
    Copies shared between profiles count once, from the fullest copy.
    """
    main = {}
    for p, k in transcripts(home / "projects"):
        main.setdefault(k, p)
    plans = [(p, transcripts(p / "projects")) for p in profiles(home, store)]
    copies: dict[str, list[Path]] = defaultdict(list)
    for _, files in plans:
        for p, k in files:
            copies[k].append(p)
    shared = {k for k, ps in copies.items() if len(ps) > 1 or k in main}

    def size(p: Path) -> int:
        try:
            return p.stat().st_size
        except OSError:
            return 0

    seen: dict[Path, set] = {}
    window = Scan(start, end)

    def uuids(p: Path) -> set:
        # A copy last written before the window shares no entry with it (a copy keeps its mtime).
        if p not in seen:
            seen[p] = ({e["uuid"] for e in _read(p) or () if isinstance(e.get("uuid"), str)}
                       if window.wants(p) else set())
        return seen[p]

    def counted_in_main(p: Path) -> Callable[[str], bool]:
        try:
            born = getattr(p.stat(), "st_birthtime", None)
        except OSError:
            born = None
        late = born is not None and cache_mtime is not None and born > cache_mtime
        return lambda d: not late or watermark is None or d > watermark

    def rules(p: Path, k: str):
        ps = sorted(copies[k], key=size, reverse=True)  # the fullest copy is the session's own record
        rank = ps.index(p)
        mp = main.get(k)
        theirs = uuids(mp) if mp else set()
        in_main = counted_in_main(mp) if mp else (lambda d: False)
        earlier = set().union(*(uuids(q) for q in ps[:rank]))
        dup = lambda u, d: isinstance(u, str) and (u in earlier or (u in theirs and in_main(d)))
        return dup, (lambda d: rank == 0 and not (mp and in_main(d)))

    out: dict[str, dict] = {}
    for profile, files in plans:
        days, first = _own_cache(profile, files, start)
        _sum(out, days)
        scan = Scan(first, end)
        for p, k in files:
            if scan.wants(p):
                scan.add(p, *(rules(p, k) if k in shared else (None, None)))
        _sum(out, scan.days)
    return {d: x for d, x in out.items() if _counts(x) and d <= end}, len(plans)


def own_days(home: Path, start: str | None, end: str) -> dict[str, dict]:
    """The default profile's days, as Claude's next ``/stats`` would cache them."""
    scan = Scan(start, end)
    for p, _ in transcripts(home / "projects"):
        scan.add(p)
    return scan.days


# -- the ledger and the sync -----------------------------------------------------


def _load_ledger(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {"version": 1, "mergedThrough": None, "days": {}}
    except (OSError, ValueError) as e:
        raise LedgerError(f"{path} is unreadable ({e}); merging without it would count every day twice") from e
    if not (isinstance(data, dict) and isinstance(data.get("days"), dict)
            and all(_is_day(d) and isinstance(r, dict) and isinstance(r.get("add"), dict)
                    and set(_blank()) <= r["add"].keys() for d, r in data["days"].items())
            and (data.get("mergedThrough") is None or _is_day(data["mergedThrough"]))):
        raise LedgerError(f"{path} is not a stats ledger; merging without it would count every day twice")
    return data


def sync(*, dry_run: bool = False, now: datetime | None = None) -> dict:
    """One pass: add every finished day the profiles have that the default cache lacks."""
    home, store = get_default_claude_config_home(), get_backup_root()
    if not (store / "sessions").is_dir():
        return {"status": "current", "profiles": 0}
    lock = FileLock(store / ".stats-sync.lock", timeout=0)
    if not lock.acquire():
        return {"status": "busy"}
    try:
        return _sync(home, store, dry_run, now)
    finally:
        lock.release()


def _sync(home: Path, store: Path, dry_run: bool, now: datetime | None) -> dict:
    cache_path, ledger_path = home / "stats-cache.json", store / LEDGER
    raw, cache, problem = _load_cache(cache_path)
    if cache is None:
        return {"status": "skipped", "reason": problem}
    try:
        cache_mtime = cache_path.stat().st_mtime
    except OSError:
        cache_mtime = None
    ledger = _load_ledger(ledger_path)
    end, c, m = yesterday(now), _watermark(cache), ledger["mergedThrough"]
    top = max(c or end, end)

    entries = _entries_of(cache)
    lost = {d: r["add"] for d, r in ledger["days"].items()
            if d <= top and not (c and d <= c and entries(d) == r.get("post"))}
    new, read = ({}, 0) if m and m >= end else side_days(home, store, _next(m), end, c, cache_mtime)
    add = {**lost, **new}
    summary = {"status": "current", "watermark": c, "mergedThrough": max(m or end, end), "profiles": read}
    if add:
        own = {}
        if c is None or any(d > c for d in add):
            own = own_days(home, _next(c), end)  # the default's own days up to `end`, as Claude would add them
            summary["watermark"] = top
        out = json.loads(raw) if raw else _empty()
        _apply(out, own)
        _apply(out, add)
        out["lastComputedDate"] = summary["watermark"]
        tokens = sum(n for x in add.values() for n in x["tokens"].values())
        summary.update(status="merged", days=len(new), reapplied=len(lost),
                       sessions=sum(x["sessions"] for x in add.values()),
                       messages=sum(x["messages"] for x in add.values()), tokens=tokens)
        entries = _entries_of(out)
        ledger["days"].update({d: {"add": x, "post": entries(d)} for d, x in add.items()})
    if dry_run:
        summary["dryRun"] = True
        return summary
    if summary["mergedThrough"] == m and not add:
        return summary
    if add and _bytes(cache_path) != raw:
        return {**summary, "status": "retry", "reason": "the stats cache changed while reading; next pass"}
    if add and raw is not None and not (store / BEFORE).exists():
        shutil.copyfile(cache_path, store / BEFORE)
    ledger["mergedThrough"] = summary["mergedThrough"]
    atomic_write_json(ledger_path, ledger)  # first: a crash before the cache write only repeats the work
    if add:
        _write_cache(cache_path, out)
    return summary


def _bytes(p: Path) -> bytes | None:
    try:
        return p.read_bytes()
    except OSError:
        return None


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="cswap stats-sync",
        description="Count every account profile's Claude Code usage in the default login's /stats "
                    "(All time): finished days are added to ~/.claude/stats-cache.json, each once.")
    parser.add_argument("--dry-run", action="store_true", help="show what would be added, write nothing")
    parser.add_argument("--json", action="store_true", help="print the result as JSON")
    args = parser.parse_args(argv)
    try:
        result = sync(dry_run=args.dry_run)
    except (LedgerError, OSError) as e:
        result = {"status": "error", "reason": str(e)}
    if args.json:
        print(json.dumps(result))
    elif result["status"] == "merged":
        print(f"Added {result['days']} day(s) from {result['profiles']} profile(s)"
              + (f", re-added {result['reapplied']}" if result["reapplied"] else "")
              + f": {result['sessions']} sessions, {result['messages']} messages, {result['tokens']:,} tokens"
              + f"; /stats now runs through {result['watermark']}." + (" (dry run)" if args.dry_run else ""))
    elif result["status"] == "current":
        print(f"Nothing new: profiles merged through {result.get('mergedThrough')}.")
    else:
        print(f"{result['status']}: {result.get('reason', 'another stats-sync is running')}", file=sys.stderr)
    return 1 if result["status"] == "error" else 0
