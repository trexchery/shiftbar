using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shiftbar;

/// A usage limit stopped the background agents of a window whose own turn was already over. `Limit`: the newest of their
/// errors; `Runs`: the workflow runs it stopped, newest first (none: plain subagents only); `Model`: what the newest ran.
public sealed record AgentStop(Limit Limit, IReadOnlyList<string> Runs, string? Model);

/// Carry on after a usage limit stopped a window's background agents. A window that launched a workflow (or background
/// subagents) ends its turn and waits. When a limit then refuses the agents, each agent's own file ends at the error, but
/// nothing is written to the window's transcript and the run never settles: the window sits idle for good, with room one
/// switch or one reset away, and no one is told. (The move pass only reports such a window: it is `Running`, so never
/// `Movable`, and closing it would lose the run.) So the window is told, in place, by one typed line.
///
/// How a stopped agent is recognised, from the files Claude Code writes under `projects/<cwd>/<session>/subagents`:
/// - a workflow agent: `workflows/<run>/agent-<id>.jsonl` ends at a usage-limit error (`ResumeStopped.LimitOf`: the error
///   is its last message, so an agent that retried and went on is not one), the run's `journal.jsonl` has its `started`
///   line and no later line for it or for its step (`Open`), and the run has not settled (`ResumeStopped.Settled`);
/// - a plain subagent: `agent-<id>.jsonl` ends at such an error. It has no journal, so only its file can say.
/// Only the stops under the window's own session folder are read (a run that outlived a /clear sits under the old id:
/// its stops are not covered), and only this process's: after the window started, and after its main loop last spoke.
///
/// When the window counts as left waiting (`StopOf`; anything else is null, and nothing is typed):
/// - its turn is over: the transcript's last message is a finished reply (`EndsTurn`). A window whose own transcript ends
///   at a limit error is not this case: that stop is its own, and stays with the notice and "Continue on";
/// - it has not moved on: no message of the user or of Claude after the stop (a reply, a "resume" typed by hand, a task
///   notification), and no slash command typed since the first of its agents stopped. `/remote-control` alone does not
///   count: Shiftbar types that itself after a switch;
/// - nothing in it is working: every agent still open in every unsettled run of this process ended at a limit error (one
///   mid-tool, one just started, or a run between agents means the run is alive); no plain subagent's file written in
///   the last `Working` ends mid-turn; no unsettled run was written in that time under another session id of the folder
///   that no live claude holds (after /clear or a fork a run goes on under the old id); and nothing under the session's
///   folder was written for `Quiet`.
///
/// When the line is sent (the tray, `CarryOnTickAsync`; Windows only, as `ConsoleNudge` is): auto-switch is on; this stop
/// was not acted on before (`Key`, kept in `carried-on.json`, so once per stop across restarts); its work did not go on
/// in another window (`Elsewhere`: "Continue on", or a second live claude on the session); the account the window runs
/// on now really has room (`Room`: another account at once, the refuser itself only `SelfRetry` after its reset, as
/// Claude Code restarts a parked run by itself then); the login is not just moving (`Settling`) and no Remote Control
/// reconnect is owed to the window (that goes first: the line starts a turn, and `/remote-control` waits for an idle
/// one); and `RemoteControl.Ready` holds (idle, quiet, the user away). The helper then looks again by itself (`rc-nudge
/// --carry-on`: the same `StopOf` and gates before typing and before Enter, the input box empty and read back exactly),
/// and `After` reads its answer. Several stopped runs get one line, keyed by the newest stop; a window that stops again
/// later (a new error) is a new stop. A closed window is never listed, and a session resumed in a new process starts
/// after its old stops.
///
/// What is logged: one line when the line was typed or given up on (`Said`: session, stop time, runs, account number;
/// never an address), and one per change of the reason for waiting. A window that cannot be typed into for half an hour
/// (text left in its box, a dialog, too narrow) gets one notice, and is tried less often after it.
public static class CarryOn
{
    /// Nothing written under the session's folder for this long: no agent of the window is mid-turn (as `OrphanQuiet`).
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);
    /// An agent whose file ends mid-turn counts as working for this long after its last write: a long tool call writes
    /// nothing meanwhile (minutes of silence are usual), and an agent killed mid-tool must not hold its window for good.
    public static readonly TimeSpan Working = TimeSpan.FromMinutes(30);
    /// The account that refused the agents has room again only this long after the refusal's reset: Claude Code restarts
    /// a parked run by itself a minute or two after it (seen at 80 s), and the stop is then gone without a word typed.
    public static readonly TimeSpan SelfRetry = TimeSpan.FromMinutes(5);
    /// A stop acted on is remembered this long: longer than a window sits idle.
    public static readonly TimeSpan Remembered = TimeSpan.FromDays(30);

    /// The stop a live window is left waiting at, or null (see the type's comment for each rule). Read from disk now.
    public static AgentStop? StopOf(LiveSession s, DateTime nowUtc)
    {
        try
        {
            if (s.Status is not ("idle" or "busy" or "shell") || ResumeStopped.TranscriptOf(s.ConfigDir, s.SessionId, s.Cwd) is not { } t) return null;
            var project = Path.GetDirectoryName(t)!;
            var folder = Path.Combine(project, s.SessionId);
            var agents = Path.Combine(folder, "subagents");
            if (!Directory.Exists(agents)) return null;
            var tail = ResumeStopped.Tail(t).ToList();
            if (!ResumeStopped.EndsTurn(tail) || Spoke(tail) is not { Reply: { } reply } spoke) return null;
            var stops = new List<(Limit Limit, string? Run, string File)>();
            // Plain subagents: only files written after the main loop last spoke, or lately, are read (a long session has hundreds).
            foreach (var f in Directory.EnumerateFiles(agents, "agent-*.jsonl"))
            {
                var wrote = File.GetLastWriteTimeUtc(f);
                var (since, lately) = (wrote > reply.UtcDateTime, wrote > s.StartedUtc && nowUtc - wrote < Working);
                if (!since && !lately) continue;
                var lines = ResumeStopped.Tail(f).ToList();
                if (ResumeStopped.LimitOf(lines) is { } l) { if (since) stops.Add((l, null, f)); }
                // One that neither stopped at a limit nor finished its reply still works: mid-tool, or waiting for the API.
                else if (lately && !ResumeStopped.EndsTurn(lines)) return null;
            }
            var runs = Path.Combine(agents, "workflows");
            foreach (var run in Directory.Exists(runs) ? Directory.EnumerateDirectories(runs, "wf_*") : [])
            {
                var (id, journal) = (Path.GetFileName(run), Path.Combine(run, "journal.jsonl"));
                // As `RunningWorkflows`: a journal older than the window is a dead process's run, a snapshot not older than the journal means it settled.
                if (!File.Exists(journal) || File.GetLastWriteTimeUtc(journal) <= s.StartedUtc || ResumeStopped.Settled(folder, id, journal)) continue;
                var open = Open(ResumeStopped.Tail(journal, int.MaxValue));
                if (open.Count == 0) return null; // between agents, or about to settle: the run is alive
                foreach (var agent in open)
                {
                    var f = Path.Combine(run, $"agent-{agent}.jsonl");
                    // One of its agents still works (or its id, read from a file, is no file name: never followed out of the run's folder).
                    if (agent.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !File.Exists(f) || ResumeStopped.LimitOf(ResumeStopped.Tail(f)) is not { } l) return null;
                    stops.Add((l, id, f));
                }
            }
            var mine = stops.Where(x => x.Limit.At > reply && x.Limit.At.UtcDateTime > s.StartedUtc).OrderByDescending(x => x.Limit.At).ToList();
            // Typed since the first of them stopped: someone was at that window while its agents were stopping.
            if (mine.Count == 0 || spoke.Typed >= mine[^1].Limit.At || nowUtc - ResumeStopped.Activity(t, s.SessionId) < Quiet) return null;
            // A run that outlived /clear or a fork keeps writing under the old session id (`RunningWorkflows` reads every folder
            // for that). Written lately under an id no live claude holds, it may be this window's, and alive: left alone.
            foreach (var w in ResumeStopped.RunningWorkflows(project, s.StartedUtc))
                if (w.SessionId != s.SessionId
                    && nowUtc - Directory.EnumerateFiles(Path.Combine(project, w.SessionId, "subagents", "workflows", w.RunId)).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() < Working
                    && !ResumeStopped.HoldsSession(s.ConfigDir, w.SessionId)) return null;
            return new AgentStop(mine[0].Limit, [.. mine.Where(x => x.Run != null).Select(x => x.Run!).Distinct()], ResumeStopped.ModelOfFile(mine[0].File));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The agents a run's journal has `started` and nothing later for, in order. A `result` closes one, and so does a later
    /// `started` of the same step (`key`): a run that restarts a refused agent after the reset starts a new one under the
    /// old key, and the refused one never gets a line again.
    internal static List<string> Open(IEnumerable<string> journal)
    {
        var open = new List<(string Key, string Id)>();
        foreach (var line in journal)
        {
            if (!line.Contains("\"agentId\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || Str(doc.RootElement, "agentId") is not { } id) continue;
                var key = Str(doc.RootElement, "key") ?? id;
                open.RemoveAll(o => o.Id == id || o.Key == key);
                if (Str(doc.RootElement, "type") == "started") open.Add((key, id));
            }
            catch (JsonException) { } // a line being written
        }
        return [.. open.Select(o => o.Id)];
    }

    /// When the main loop last spoke (`Reply`: the stamp of the transcript's newest message, null when it has none or no
    /// stamp), and the newest stamp of a slash command typed after it (`Typed`; unstamped counts as just now). A command is
    /// a `system`/`local_command` line (Claude Code 2.1.3xx) or a local user line (older); its output lines are skipped, and
    /// so is `/remote-control`, which Shiftbar types itself.
    internal static (DateTimeOffset? Reply, DateTimeOffset Typed) Spoke(IReadOnlyList<string> tail)
    {
        var typed = DateTimeOffset.MinValue;
        for (var i = tail.Count - 1; i >= 0; i--)
        {
            try
            {
                using var doc = JsonDocument.Parse(tail[i]);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                var type = Str(r, "type");
                var at = r.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && ts.TryGetDateTimeOffset(out var v) ? v : (DateTimeOffset?)null;
                var command = type == "system" && Str(r, "subtype") == "local_command" ? Str(r, "content") ?? ""
                    : type == "user" && ResumeStopped.IsLocal(r) ? r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "content") ?? "" : ""
                    : null;
                if (command != null)
                {
                    if (command.StartsWith("<command-name>") && !command.StartsWith("<command-name>/remote-control<") && (at ?? DateTimeOffset.MaxValue) > typed)
                        typed = at ?? DateTimeOffset.MaxValue;
                    continue;
                }
                if (type is "user" or "assistant") return (at, typed);
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        return (null, typed);
    }

    /// Whether the account the window runs on now (`on`: the default login's, or its profile's) really has room for the
    /// stopped work: read live and below its switch `line` on what the agents' model spends (model unknown, an agent
    /// refused on its first request: on every window), not refused until a reset still ahead (the account's, or that
    /// model's own; unknown: any model's), and either it is not the account that refused the agents (`refuser`, as
    /// `ResumeStopped.Refuser` charges the error; unknown: only when the default login moved after the stop,
    /// `movedSince`) or that refusal's reset passed `SelfRetry` ago. Every doubt is no room: the window waits.
    public static bool Room(Account? on, AgentStop stop, int? refuser, bool movedSince, double line, DateTimeOffset now,
        IReadOnlyDictionary<int, DateTimeOffset> refusedUntil, IReadOnlyDictionary<(int Account, string Scope), DateTimeOffset> scopedRefused) =>
        on is { Disabled: false, UsageStatus: "ok", Usage: not null } && (stop.Model is { } model ? ResumeStopped.PeakFor(on, model) : Format.Peak(on)) < line
        && !(refusedUntil.GetValueOrDefault(on.Number) > now)
        && !scopedRefused.Any(r => r.Key.Account == on.Number && r.Value > now && stop.Model?.Contains(r.Key.Scope, StringComparison.OrdinalIgnoreCase) != false)
        && ((refuser is { } r ? r != on.Number : movedSince) || stop.Limit.ResetsAt + SelfRetry <= now);

    /// One stop of one window: its session and the newest error's time.
    public static string Key(string sessionId, AgentStop stop) => $"{sessionId}@{stop.Limit.At.ToUnixTimeMilliseconds()}";

    /// Why the tray types nothing for a standing stop, or null when its window is due the line. `told`: stops already
    /// acted on, as loaded from disk; that one (`Told`) is silent, not logged.
    public static string? Hold(bool autoSwitch, IReadOnlyDictionary<string, DateTime> told, string key, bool room) =>
        !autoSwitch ? "auto-switch off" : told.ContainsKey(key) ? Told : !room ? "no room yet" : null;
    public const string Told = "told";

    /// Whether the stop's work already went on in another window, so this one is left as it is: the session was reopened
    /// or continued on an account at or after the stop (`resumed`: the tray's "session id#account" → when, as its
    /// `Continued`), or a second live claude holds the session (`all`: every live window).
    public static bool Elsewhere(IReadOnlyDictionary<string, DateTime> resumed, IEnumerable<LiveSession> all, LiveSession s, AgentStop stop) =>
        resumed.Any(m => m.Key.StartsWith(s.SessionId + "#", StringComparison.Ordinal) && m.Value >= stop.Limit.At.UtcDateTime)
        || all.Any(o => o.SessionId == s.SessionId && o.Pid != s.Pid);

    /// Whether the default login is just moving: the Remote Control side has not seen today's login yet (`seen`: it queues
    /// its reconnects when it does, a moment later), or the engine wrote the switch under `RcReconnect.FirstWait` ago. The
    /// line waits: typed first, it would start a turn that `/remote-control` then has to wait out.
    public static bool Settling(int? seen, int? active, DateTime loginChangedUtc, DateTime nowUtc) =>
        seen != active || loginChangedUtc != DateTime.MaxValue && nowUtc - loginChangedUtc < RcReconnect.FirstWait;

    /// What the helper's answer means for a stop. `Done`: acted on for good (typed and entered; typed and left in a box
    /// that read otherwise; or typed and taken out again `RcReconnect.MaxTries` times), so it is remembered and never
    /// tried again. Else it is tried again after `Wait`: a helper that typed nothing (`refused`, `error`) a minute later,
    /// and every ten minutes once it has for `Patience` tries, when `Tell` says, once, to tell the owner.
    public static (bool Done, int Tries, int Refusals, TimeSpan Wait, bool Tell) After(string result, int tries, int refusals)
    {
        var word = result.Split(' ', 2)[0];
        if (word == "aborted") tries++;
        else if (word is not ("sent" or "left")) refusals++;
        return (word is "sent" or "left" || tries >= RcReconnect.MaxTries, tries, refusals,
            TimeSpan.FromMinutes(refusals >= Patience ? 10 : 1), word is not ("sent" or "left" or "aborted") && refusals == Patience);
    }
    public const int Patience = 30;

    /// What is typed: who speaks, what happened, what to do. It must fit one row of the prompt (a wrapped line could not be
    /// read back as typed), so the fullest wording that fits `width` characters; null when not even the shortest does.
    /// A run id is named only when it is the plain `wf_…` Claude Code makes.
    public static string? Line(AgentStop stop, int width)
    {
        var at = stop.Limit.At.ToLocalTime().ToString("HH:mm");
        var named = stop.Runs.Where(r => Regex.IsMatch(r, "^wf_[A-Za-z0-9-]{1,40}$")).ToList();
        var run = named.Count == 0 ? "" : $" (workflow {named[0]}{(stop.Runs.Count > 1 ? $" and {stop.Runs.Count - 1} more" : "")})";
        var what = $"Shiftbar: a usage limit stopped this window's background agents at {at}";
        return new[]
        {
            $"{what}{run}. The account has room again: carry on and resume the stopped work.",
            $"{what}{run}. There is room again: resume them.",
            $"{what}. There is room again: resume them.", // fits a 120-column window
            "Shiftbar: a usage limit stopped your background agents. Resume them.",
        }.FirstOrDefault(l => l.Length <= width);
    }

    /// The log line for a stop acted on: the session, what the helper answered, when the agents stopped, in which runs,
    /// and the number of the account the window runs on. Never an address or a name.
    public static string Said(string sessionId, AgentStop stop, int? account, string result) =>
        $"carry on: {sessionId} {result} (agents stopped at a usage limit {stop.Limit.At:u}{(stop.Runs.Count > 0 ? ", " + string.Join(' ', stop.Runs) : "")}; room on #{account?.ToString() ?? "?"})";

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// The tray's side: from the watch poll, after the Remote Control queue, so the two never type at once.
public sealed partial class TrayApp
{
    readonly Dictionary<string, DateTime> _carried = ResumeStopped.LoadMoves(CarriedPath); // `CarryOn.Key` → when that stop was acted on: once per stop, across restarts
    readonly Dictionary<string, (string? Why, int Tries, int Refusals, DateTime NotBefore)> _carrying = []; // per stop: the last reason for waiting (logged once per change), lines typed and taken out again, helpers that typed nothing, the next try
    bool _carryBusy;
    DateTime _carryNext;

    static string CarriedPath => Path.Combine(DockSettings.Dir, "carried-on.json");

    /// Every live window left waiting at a stop of its background agents (`CarryOn.StopOf`) is told to carry on, once,
    /// as soon as its account has room: after a switch of the default login, or `CarryOn.SelfRetry` after the refusal's reset.
    async Task CarryOnTickAsync()
    {
        if (_carryBusy || !_settings.AutoSwitch || !OperatingSystem.IsWindows() || _limits.Switching || _snap is not { } snap || DateTime.UtcNow < _carryNext) return;
        (_carryBusy, _carryNext) = (true, DateTime.UtcNow.AddSeconds(15));
        try
        {
            var (all, found, login) = await Task.Run(() =>
            {
                var all = ResumeStopped.LiveSessions(ResumeStopped.ConfigDirs(snap.ActiveAccountNumber));
                return (all, all.Select(s => (Window: s, Stop: CarryOn.StopOf(s, DateTime.UtcNow))).Where(x => x.Stop != null).Select(x => (x.Window, Stop: x.Stop!)).ToList(), ResumeStopped.DefaultLogin());
            });
            // The login just moved: its Remote Control reconnects are queued first (RcObserve, then RcQueueAsync).
            if (CarryOn.Settling(_rcActive, snap.ActiveAccountNumber, login.ChangedAt, DateTime.UtcNow)) return;
            // The login's record is trusted only while it agrees with the listing, as in MovePassAsync.
            var trusted = login.Account == snap.ActiveAccountNumber ? login : (snap.ActiveAccountNumber, DateTime.MaxValue);
            foreach (var (s, stop) in found)
            {
                var (key, now) = (CarryOn.Key(s.SessionId, stop), DateTime.UtcNow);
                var state = _carrying.GetValueOrDefault(key);
                if (now < state.NotBefore || CarryOn.Elsewhere(_resumed, all, s, stop)) continue;
                var on = snap.Accounts.FirstOrDefault(a => a.Number == s.Account);
                var moved = s.ConfigDir == ResumeStopped.DefaultDir && login.Account == snap.ActiveAccountNumber && stop.Limit.At.UtcDateTime < login.ChangedAt;
                var room = CarryOn.Room(on, stop, ResumeStopped.Refuser(stop.Limit, ResumeStopped.ProfileAccount(s.ConfigDir), snap.Accounts, trusted), moved,
                    on == null ? 0 : _line.For(on, stop.Model), DateTimeOffset.UtcNow, _limits.RefusedUntil, _limits.ScopedRefusedUntil);
                var why = CarryOn.Hold(_settings.AutoSwitch, _carried, key, room);
                if (why == CarryOn.Told) continue;
                // Its Remote Control reconnect goes first: the line starts a turn, and `/remote-control` waits for an idle window.
                why ??= (_rc.Entries.Any(e => e.SessionId == s.SessionId) ? "remote control first" : null)
                    ?? await Task.Run(() => ResumeStopped.Orphaned(s.Pid) ? "terminal closed" : RemoteControl.Ready(s.ConfigDir, s.Pid, s.SessionId, manual: false));
                var result = why == null ? await ConsoleNudge.RunAsync(Environment.ProcessPath!, s.Pid, s.SessionId, manual: false, carryOn: true) : null;
                var after = result == null ? (Done: false, state.Tries, state.Refusals, Wait: TimeSpan.Zero, Tell: false) : CarryOn.After(result, state.Tries, state.Refusals);
                var folder = Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
                if (after.Done)
                {
                    // Typed and entered; or typed and something else sits in the box; or typed and taken out twice: this stop is done with.
                    _carried[key] = now;
                    ResumeStopped.SaveMoves(CarriedPath, _carried, now, CarryOn.Remembered);
                    _carrying.Remove(key);
                    Program.Log(CarryOn.Said(s.SessionId, stop, s.Account, result!));
                    if (result!.StartsWith("sent")) Notify("", $"{folder}: a usage limit had stopped its background agents. It was told to carry on.", ToolTipIcon.None);
                    else if (result.StartsWith("left")) Notify($"{folder}: its background agents stopped at a usage limit",
                        "Its input box did not read as expected after typing, so Enter was not pressed. Check that window.", ToolTipIcon.Warning);
                    continue;
                }
                why ??= result;
                if (state.Why != why) Program.Log($"carry on: {s.SessionId} waiting ({why})");
                // Half an hour of a window that cannot be typed into: the owner is told once, and it is tried less often (never given up: the box may clear).
                if (after.Tell) Notify($"{folder}: its background agents stopped at a usage limit",
                    $"There is room again, but Shiftbar could not type into that window ({why}). Tell it to carry on yourself.", ToolTipIcon.Warning);
                // A helper that typed nothing is tried again in a minute, not at every look.
                _carrying[key] = (why, after.Tries, after.Refusals, result == null ? default : now + after.Wait);
            }
        }
        catch (Exception e) { Program.Log(e); }
        finally { _carryBusy = false; }
    }
}
