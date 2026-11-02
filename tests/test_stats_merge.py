"""cswap stats-sync: side profiles' usage folded into the default /stats cache, once."""

import json
import os
import uuid
from datetime import datetime, timezone
from pathlib import Path

import pytest

from claude_swap import paths, stats_merge as sm

NOW = datetime(2026, 11, 3, 12, 0, tzinfo=timezone.utc)  # yesterday: 2026-11-02


def entry(ts, kind="user", uid=None, model="claude-x", tokens=(1, 2, 3, 4), tools=0, **extra):
    e = {"type": kind, "timestamp": ts, "uuid": uid or str(uuid.uuid4()), **extra}
    if kind == "assistant":
        usage = dict(zip(sm._USAGE, tokens))
        e["message"] = {"model": model, "usage": usage,
                        "content": [{"type": "tool_use"}] * tools + [{"type": "text"}]}
    return e


def write(path: Path, entries) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("".join(json.dumps(e) + "\n" for e in entries), encoding="utf-8")
    return path


def session(d, n=2, prefix="", tools=1):
    """n user/assistant turns on day d, uuids prefix0.."""
    out = []
    for i in range(n):
        out.append(entry(f"{d}T10:0{i}:00.000Z", uid=f"{prefix}u{i}"))
        out.append(entry(f"{d}T10:0{i}:30.000Z", "assistant", uid=f"{prefix}a{i}", tools=tools))
    return out


@pytest.fixture
def env(temp_home):
    home = temp_home / ".claude"
    store = paths.get_backup_root()
    (store / "sessions").mkdir(parents=True)

    class Env:
        cache = home / "stats-cache.json"
        ledger = store / sm.LEDGER

        @staticmethod
        def main(name, entries):
            return write(home / "projects" / "C--w" / name, entries)

        @staticmethod
        def side(n, name, entries):
            return write(store / "sessions" / f"{n}-a{n}_x.com" / "projects" / "C--w" / name, entries)

        @staticmethod
        def seed(**over):
            c = {**sm._empty(), "lastComputedDate": "2026-11-02",
                 "dailyActivity": [{"date": "2026-11-01", "messageCount": 5, "sessionCount": 1, "toolCallCount": 0}],
                 "dailyModelTokens": [{"date": "2026-11-01", "tokensByModel": {"claude-x": 10}}],
                 "modelUsage": {"claude-x": {**{k: 0 for k in sm._MU}, "inputTokens": 10, "webSearchRequests": 0,
                                             "costUSD": 0, "contextWindow": 0, "maxOutputTokens": 0}},
                 "totalSessions": 1, "totalMessages": 5, "hourCounts": {"9": 1}, **over}
            Env.cache.write_text(json.dumps(c, indent=2), encoding="utf-8")
            return c

        @staticmethod
        def read():
            return json.loads(Env.cache.read_text(encoding="utf-8"))

        @staticmethod
        def day(c, d):
            a = next((e for e in c["dailyActivity"] if e["date"] == d), None)
            t = next((e["tokensByModel"] for e in c["dailyModelTokens"] if e["date"] == d), None)
            return a, t

    return Env


def test_backfills_side_days_once(env):
    env.seed()
    env.side(1, "s1.jsonl", session("2026-11-01"))
    write(env.side(1, "s1.jsonl", session("2026-11-01")).parent / "s1" / "subagents" / "agent-1.jsonl",
          [entry("2026-11-01T10:05:00.000Z", "assistant", tools=3, isSidechain=True)])

    r = sm.sync(now=NOW)

    assert (r["status"], r["days"], r["sessions"], r["messages"]) == ("merged", 1, 1, 4)
    c = env.read()
    a, t = env.day(c, "2026-11-01")
    # 4 messages and 2 tool calls from the session; the subagent adds tokens (and its tool calls: the day is active)
    assert a == {"date": "2026-11-01", "messageCount": 9, "sessionCount": 2, "toolCallCount": 5}
    assert t == {"claude-x": 10 + 3 * 10}
    assert (c["totalSessions"], c["totalMessages"], c["lastComputedDate"]) == (2, 9, "2026-11-02")
    assert c["modelUsage"]["claude-x"]["inputTokens"] == 10 + 3
    assert sum(c["hourCounts"].values()) == 2
    assert c["longestSession"]["sessionId"] == "s1" and c["firstSessionDate"] == "2026-11-01T10:00:00.000Z"

    before = env.cache.read_bytes()
    assert sm.sync(now=NOW)["status"] == "current"
    assert env.cache.read_bytes() == before  # idempotent: nothing added twice
    assert (paths.get_backup_root() / sm.BEFORE).exists()  # the untouched cache, kept once


def test_advances_the_watermark_with_the_defaults_own_days(env):
    env.seed(lastComputedDate="2026-10-31", dailyActivity=[], dailyModelTokens=[], totalSessions=0, totalMessages=0,
             modelUsage={}, hourCounts={})
    env.main("m1.jsonl", session("2026-11-01", prefix="m") + session("2026-11-03", prefix="t"))  # 11-03 is today
    env.side(1, "s1.jsonl", session("2026-11-02", n=1))

    assert sm.sync(now=NOW)["watermark"] == "2026-11-02"
    c = env.read()
    assert env.day(c, "2026-11-01")[0]["messageCount"] == 4  # the default's own day, as Claude would add it
    assert env.day(c, "2026-11-02")[0]["messageCount"] == 2  # the side's
    assert env.day(c, "2026-11-03") == (None, None)  # today stays live
    assert (c["lastComputedDate"], c["totalSessions"], c["totalMessages"]) == ("2026-11-02", 2, 6)


def test_no_cache_yet_gets_the_full_history(env):
    env.main("m1.jsonl", session("2026-10-01", prefix="m"))
    env.side(1, "s1.jsonl", session("2026-10-02"))
    assert sm.sync(now=NOW)["status"] == "merged"
    c = env.read()
    assert (c["version"], c["lastComputedDate"], c["totalMessages"], c["totalSessions"]) == (5, "2026-11-02", 8, 2)


def test_a_stats_write_that_crossed_ours_is_readded_not_doubled(env):
    env.seed()
    env.side(1, "s1.jsonl", session("2026-11-01"))
    pre = env.cache.read_bytes()
    sm.sync(now=NOW)
    merged = env.read()

    # Claude read the cache before our write and saved after it: our day is gone, its own new day is there.
    lost = json.loads(pre)
    lost["dailyActivity"].append({"date": "2026-11-02", "messageCount": 7, "sessionCount": 1, "toolCallCount": 0})
    lost["totalMessages"] += 7
    lost["totalSessions"] += 1
    env.cache.write_text(json.dumps(lost), encoding="utf-8")

    assert sm.sync(now=NOW)["reapplied"] == 1
    c = env.read()
    assert env.day(c, "2026-11-01") == env.day(merged, "2026-11-01")
    assert (c["totalMessages"], c["totalSessions"]) == (merged["totalMessages"] + 7, merged["totalSessions"] + 1)
    assert sm.sync(now=NOW)["status"] == "current"


def test_a_crash_between_ledger_and_cache_repeats_the_work_once(env, monkeypatch):
    env.seed()
    env.side(1, "s1.jsonl", session("2026-11-01"))
    with monkeypatch.context() as m:
        m.setattr(sm, "_write_cache", lambda *a: (_ for _ in ()).throw(OSError("killed")))
        with pytest.raises(OSError):
            sm.sync(now=NOW)
    assert env.ledger.exists() and env.read()["totalMessages"] == 5
    assert sm.sync(now=NOW)["reapplied"] == 1
    assert env.read()["totalMessages"] == 9
    assert sm.sync(now=NOW)["status"] == "current"


def test_a_wiped_cache_gets_the_side_days_back(env):
    env.seed()
    env.side(1, "s1.jsonl", session("2026-11-01"))
    sm.sync(now=NOW)
    env.seed()  # /stats rebuilt the cache from the default's own transcripts
    sm.sync(now=NOW)
    assert env.read()["totalMessages"] == 9


def test_copied_transcripts_count_once(env):
    shared = session("2026-11-01", prefix="p")
    env.main("s.jsonl", shared)  # Claude counts this copy itself
    env.side(1, "s.jsonl", shared + session("2026-11-02", n=1, prefix="q"))  # the window went on on account 1
    only = session("2026-11-01", prefix="o")
    env.side(2, "o.jsonl", only)  # reopened twice: an early copy on 2, the fuller one on 3
    env.side(3, "o.jsonl", only + session("2026-11-01", n=1, prefix="r"))
    env.seed(dailyActivity=[], dailyModelTokens=[], totalSessions=0, totalMessages=0, modelUsage={}, hourCounts={})
    later = env.cache.stat().st_mtime + 60
    os.utime(env.cache, (later, later))  # cached after the default's copy existed

    r = sm.sync(now=NOW)

    c = env.read()
    assert env.day(c, "2026-11-01")[0] == {"date": "2026-11-01", "messageCount": 6, "sessionCount": 1, "toolCallCount": 3}
    assert env.day(c, "2026-11-02")[0] == {"date": "2026-11-02", "messageCount": 2, "sessionCount": 0, "toolCallCount": 1}
    assert (r["sessions"], r["messages"]) == (1, 8)


@pytest.mark.skipif(not hasattr(os.stat_result, "st_birthtime"), reason="needs file birth times")
def test_a_copy_that_reached_the_default_after_its_day_was_cached_counts_on_the_side(env):
    env.seed(dailyActivity=[], dailyModelTokens=[], totalSessions=0, totalMessages=0, modelUsage={}, hourCounts={})
    old = datetime(2026, 11, 2, 1, tzinfo=timezone.utc).timestamp()
    os.utime(env.cache, (old, old))  # Claude cached 11-01 before the copy below existed
    shared = session("2026-11-01", prefix="p")
    env.side(1, "s.jsonl", shared)
    env.main("s.jsonl", shared)
    assert sm.sync(now=NOW)["messages"] == 4


def test_a_new_day_reads_only_files_written_since(env, monkeypatch):
    def stamp(p, day):
        t = datetime.fromisoformat(f"{day}T12:00:00+00:00").timestamp()
        os.utime(p, (t, t))

    env.seed()
    stamp(env.side(1, "s1.jsonl", session("2026-11-01")), "2026-11-01")
    stamp(env.main("s1.jsonl", session("2026-11-01")), "2026-11-01")  # an old copy shared with the default
    stamp(env.side(2, "s2.jsonl", session("2026-11-03")), "2026-11-03")
    sm.sync(now=NOW)
    reads = []
    real = sm._read
    monkeypatch.setattr(sm, "_read", lambda p: reads.append(p.name) or real(p))
    assert sm.sync(now=datetime(2026, 11, 4, 12, tzinfo=timezone.utc))["days"] == 1
    assert reads == ["s2.jsonl"]


def test_unknown_cache_format_is_left_alone(env):
    env.seed(version=6)
    env.side(1, "s1.jsonl", session("2026-11-01"))
    before = env.cache.read_bytes()
    assert sm.sync(now=NOW)["status"] == "skipped"
    assert env.cache.read_bytes() == before and not env.ledger.exists()


def test_an_unreadable_ledger_stops_the_merge(env, capsys):
    env.seed()
    env.side(1, "s1.jsonl", session("2026-11-01"))
    env.ledger.write_text("{", encoding="utf-8")
    before = env.cache.read_bytes()
    assert sm.main(["--json"]) == 1
    assert json.loads(capsys.readouterr().out)["status"] == "error"
    assert env.cache.read_bytes() == before


def test_dry_run_writes_nothing(env):
    env.seed()
    env.side(1, "s1.jsonl", session("2026-11-01"))
    before = env.cache.read_bytes()
    assert sm.sync(dry_run=True, now=NOW)["messages"] == 4
    assert env.cache.read_bytes() == before and not env.ledger.exists()


def test_cleaned_up_days_come_from_the_profiles_own_cache(env):
    env.seed()
    profile = paths.get_backup_root() / "sessions" / "1-a1_x.com"
    t = env.side(1, "s1.jsonl", session("2026-11-01"))
    stamp = datetime(2026, 11, 1, 12, tzinfo=timezone.utc).timestamp()
    os.utime(t, (stamp, stamp))  # the profile's oldest surviving transcript
    own = {**sm._empty(), "lastComputedDate": "2026-11-01",
           "dailyActivity": [{"date": "2026-10-20", "messageCount": 30, "sessionCount": 2, "toolCallCount": 4},
                             {"date": "2026-11-01", "messageCount": 4, "sessionCount": 1, "toolCallCount": 2}],
           "dailyModelTokens": [{"date": "2026-10-20", "tokensByModel": {"claude-y": 300}},
                                {"date": "2026-11-01", "tokensByModel": {"claude-x": 40}}],
           "modelUsage": {"claude-y": {"inputTokens": 100, "outputTokens": 200, "cacheReadInputTokens": 0,
                                       "cacheCreationInputTokens": 0},
                          "claude-x": {"inputTokens": 4, "outputTokens": 8, "cacheReadInputTokens": 12,
                                       "cacheCreationInputTokens": 16}},
           "totalSessions": 3, "totalMessages": 34, "hourCounts": {"8": 3},
           "longestSession": {"sessionId": "gone", "duration": 9e6, "messageCount": 30,
                              "timestamp": "2026-10-20T08:00:00.000Z"},
           "firstSessionDate": "2026-10-20T08:00:00.000Z"}
    (profile / "stats-cache.json").write_text(json.dumps(own), encoding="utf-8")

    sm.sync(now=NOW)

    c = env.read()
    assert env.day(c, "2026-10-20") == ({"date": "2026-10-20", "messageCount": 30, "sessionCount": 2, "toolCallCount": 4},
                                       {"claude-y": 300})
    assert env.day(c, "2026-11-01")[0]["messageCount"] == 9  # the transcripts', not the cache's copy of it
    assert c["modelUsage"]["claude-y"]["outputTokens"] == 200
    assert c["hourCounts"]["8"] == 2  # two of the cache's three sessions came before its transcripts
    assert c["longestSession"]["sessionId"] == "gone" and c["firstSessionDate"] == "2026-10-20T08:00:00.000Z"


def test_a_profile_linked_to_the_default_projects_is_not_read(env):
    env.seed()
    env.main("m.jsonl", session("2026-11-01"))
    link = paths.get_backup_root() / "sessions" / "1-a1_x.com" / "projects"
    link.parent.mkdir(parents=True)
    try:
        link.symlink_to(env.cache.parent / "projects", target_is_directory=True)
    except OSError:
        pytest.skip("symlinks not permitted here")
    assert sm.sync(now=NOW) == {"status": "current", "watermark": "2026-11-02", "mergedThrough": "2026-11-02",
                                "profiles": 0}


def test_scan_reads_transcripts_as_claude_does(tmp_path, monkeypatch):
    p = write(tmp_path / "proj" / "s.jsonl", [
        entry("2026-11-01T10:00:00.000Z"),
        entry("2026-11-01T10:00:01.000Z", isSidechain=True),  # a sidechain line in a main transcript: not counted
        entry("2026-11-01T10:00:02.000Z", "assistant", model="<synthetic>"),  # counts as a message, no tokens
        entry("2026-11-01T10:00:03.000Z", "progress"),  # not an entry /stats reads
        entry("2026-11-02T10:00:00.000Z", "assistant", tools=2, tokens=(0, 0, 0, 0)),
    ])
    with p.open("a", encoding="utf-8") as f:
        f.write("{not json\n")
    s = sm.Scan(None, "2026-11-02")
    s.add(p)
    assert (s.days["2026-11-01"]["messages"], s.days["2026-11-01"]["tokens"]) == (2, {})
    d2 = s.days["2026-11-02"]
    assert (d2["messages"], d2["tools"], d2["tokens"], d2["usage"]) == (1, 2, {}, {"claude-x": [0, 0, 0, 0]})
    assert s.days["2026-11-01"]["longest"]["duration"] == 86_400_000

    # A subagent's tool calls count only on a day a main transcript made active.
    sub = write(tmp_path / "proj" / "s" / "subagents" / "agent-1.jsonl",
                [entry("2026-11-02T11:00:00.000Z", "assistant", tools=1), entry("2026-11-05T11:00:00.000Z", "assistant", tools=1)])
    s = sm.Scan(None, None)
    s.add(p)
    s.add(sub)
    assert s.days["2026-11-02"]["tools"] == 3 and s.days["2026-11-05"]["tools"] == 0
    assert s.days["2026-11-05"]["tokens"] == {"claude-x": 10} and not s.days["2026-11-05"]["active"]
    assert sm.transcripts(tmp_path) == [(p, "s.jsonl"), (sub, "s/subagents/agent-1.jsonl")]

    # Past 100 MiB only the tail is read, from its first whole line: here the session starts on 11-02.
    data = p.read_bytes()
    last = data.rfind(b"\n", 0, data.index(b'"2026-11-02T10:00:00.000Z"')) + 1
    monkeypatch.setattr(sm, "TAIL", len(data) - last + 3)  # cut 3 bytes into the line before it
    s = sm.Scan(None, None)
    s.add(p)
    assert "2026-11-01" not in s.days and s.days["2026-11-02"]["sessions"] == 1

    # A window skips files last written before it starts.
    old = datetime(2026, 10, 1, tzinfo=timezone.utc).timestamp()
    os.utime(p, (old, old))
    s = sm.Scan("2026-11-01", None)
    s.add(p)
    assert s.days == {}


def test_yesterday_is_a_local_day_back_in_utc():
    assert sm.yesterday(NOW) == "2026-11-02"
    assert sm.yesterday(datetime(2026, 11, 3, 0, 30, tzinfo=timezone.utc)) == "2026-11-02"


def test_cli_dispatches_stats_sync(env, monkeypatch, capsys):
    from claude_swap import cli

    env.seed()
    monkeypatch.setattr("sys.argv", ["cswap", "stats-sync", "--json", "--dry-run"])
    with pytest.raises(SystemExit) as done:
        cli.main()
    assert done.value.code == 0
    assert json.loads(capsys.readouterr().out)["status"] in ("current", "merged")
