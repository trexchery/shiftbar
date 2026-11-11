using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

/// Synthetic transcripts, journals and agent files, shaped like Claude Code 2.1.3xx's: a window that launched a workflow
/// ends its turn (`end_turn`, then a `turn_duration` line counting the pending run) and writes nothing more; each agent's
/// file ends at the API's refusal; the run's journal has `launched`, a `started` per agent and a `result` only for an agent
/// that came back; `<session>/workflows/<run>.json` appears when the run settles. Every value is invented. Stamps are
/// relative to now: the staged files are written now, and a stop must be the live process's own.
public class CarryOnTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    static readonly DateTimeOffset Reply = Now.AddMinutes(-40), Stop1 = Now.AddMinutes(-20), Stop2 = Now.AddMinutes(-15), Reset = Now.AddHours(2);
    /// A look three minutes from now: everything staged has been quiet for `CarryOn.Quiet`.
    static DateTime Later => DateTime.UtcNow.AddMinutes(3);

    string Project => Path.Combine(_t.Path, "projects", "C--w");
    string Transcript => Path.Combine(Project, "sid.jsonl");
    string RunDir(string run) => Path.Combine(Project, "sid", "subagents", "workflows", run);
    LiveSession Window(string status = "busy", DateTime? started = null) => new(4242, "sid", @"C:\w", _t.Path, 1, status, started ?? Now.UtcDateTime.AddHours(-3));
    AgentStop? Stop(string status = "busy") => CarryOn.StopOf(Window(status), Later);

    static string At(DateTimeOffset t) => t.ToString("O");
    static string Asked(DateTimeOffset t, string text = "Run the checks.") => $$$"""{"type":"user","timestamp":"{{{At(t)}}}","message":{"role":"user","content":"{{{text}}}"}}""";
    static string Done(DateTimeOffset t) => $$$"""{"type":"assistant","timestamp":"{{{At(t)}}}","message":{"role":"assistant","model":"claude-fable-5-2","stop_reason":"end_turn","content":[{"type":"text","text":"The workflow is running; I will report when it settles."}]}}""";
    static string TurnTime(DateTimeOffset t) => $$$"""{"type":"system","subtype":"turn_duration","durationMs":1000,"pendingWorkflowCount":1,"timestamp":"{{{At(t)}}}"}""";
    /// A slash command as 2.1.3xx records it, and its (empty) output line.
    static string[] Command(string name, DateTimeOffset t) =>
    [
        $$$"""{"type":"system","subtype":"local_command","content":"<command-name>/{{{name}}}</command-name>\n            <command-message>{{{name}}}</command-message>\n            <command-args></command-args>","level":"info","timestamp":"{{{At(t)}}}"}""",
        $$$"""{"type":"system","subtype":"local_command","content":"<local-command-stdout></local-command-stdout>","level":"info","timestamp":"{{{At(t)}}}"}""",
    ];
    /// What Shiftbar's own reconnect leaves in the transcript after a switch.
    static string[] Reconnect(DateTimeOffset t) =>
    [
        .. Command("remote-control", t),
        """{"type":"bridge-session","sessionId":"sid","bridgeSessionId":"cse_02","lastSequenceNum":0,"ownerAccountUuid":"4b1c2d3e-0000-4000-8000-00000000000b"}""",
        $$$"""{"type":"system","subtype":"bridge_status","content":"/remote-control is active","timestamp":"{{{At(t.AddSeconds(1))}}}"}""",
    ];

    static string Tool(string agent, DateTimeOffset t) => $$$"""{"isSidechain":true,"agentId":"{{{agent}}}","type":"assistant","timestamp":"{{{At(t)}}}","message":{"role":"assistant","model":"claude-fable-5-2","stop_reason":"tool_use","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}""";
    static string ToolResult(string agent, DateTimeOffset t) => $$$"""{"isSidechain":true,"agentId":"{{{agent}}}","type":"user","timestamp":"{{{At(t)}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    static string Request(string agent, DateTimeOffset t) => $$$"""{"type":"api-request","agentId":"{{{agent}}}","timestamp":"{{{At(t)}}}","querySource":"agent:builtin:workflow-subagent"}""";
    static string Refused(string agent, DateTimeOffset t) => $$$"""{"isSidechain":true,"agentId":"{{{agent}}}","type":"assistant","timestamp":"{{{At(t)}}}","apiError":"usage_limit_reached","apiErrorParams":{"rate_limit_info":{"status":"rejected","resetsAt":{{{Reset.ToUnixTimeSeconds()}}},"rateLimitType":"five_hour"}},"error":"rate_limit","isApiErrorMessage":true,"apiErrorStatus":429,"message":{"role":"assistant","model":"<synthetic>","stop_reason":"stop_sequence","content":[{"type":"text","text":"You've hit your 5-hour limit · resets 2:00am (UTC)"}]}}""";
    /// An agent's file that ends at the refusal: its last tool result, the request, the error.
    static string[] Dead(string agent, DateTimeOffset t) => [Tool(agent, t.AddSeconds(-9)), ToolResult(agent, t.AddSeconds(-1)), Request(agent, t), Refused(agent, t)];

    /// `step`: the agent whose step this one takes up again (a run restarts a refused agent under the old key).
    static string Started(string agent, string? step = null) => $$$"""{"type":"started","key":"v2:k-{{{step ?? agent}}}","agentId":"{{{agent}}}","label":"build:step","phase":"Build"}""";
    static string Result(string agent, string? step = null) => $$$"""{"type":"result","key":"v2:k-{{{step ?? agent}}}","agentId":"{{{agent}}}","result":{"ok":true},"toolsUsed":["Bash"]}""";

    void Main(params string[] lines) { Directory.CreateDirectory(Project); File.WriteAllLines(Transcript, lines); }
    void Journal(string run, params string[] lines) { Directory.CreateDirectory(RunDir(run)); File.WriteAllLines(Path.Combine(RunDir(run), "journal.jsonl"), ["""{"type":"launched"}""", .. lines]); }
    void Agent(string run, string agent, params string[] lines) => File.WriteAllLines(Path.Combine(RunDir(run), $"agent-{agent}.jsonl"), lines);
    string Snapshot(string run) => Path.Combine(Project, "sid", "workflows", run + ".json");
    void Settle(string run)
    {
        Directory.CreateDirectory(Path.Combine(Project, "sid", "workflows"));
        File.WriteAllText(Snapshot(run), """{"status":"killed"}""");
    }
    string Plain(string agent, params string[] lines)
    {
        var file = Path.Combine(Project, "sid", "subagents", $"agent-{agent}.jsonl");
        File.WriteAllLines(file, lines);
        return file;
    }

    /// The reported case: the window ended its turn with one run pending, then the run's two agents each ended at the limit.
    void Tonight()
    {
        Main(Asked(Reply.AddMinutes(-10)), Done(Reply), TurnTime(Reply.AddSeconds(3)));
        Journal("wf_x", Started("a1"), Started("a2"));
        Agent("wf_x", "a1", Dead("a1", Stop1));
        Agent("wf_x", "a2", Dead("a2", Stop2));
    }

    static Account A(int n, double five, bool active = false, DateTimeOffset? resets = null, string status = "ok", bool disabled = false, double? fable = null) =>
        new(n, $"user{n}@example.com", active, status, status == "ok" ? new Usage(new Window(five, resets), new Window(10, null), fable is { } f ? [new Window(f, null, "Fable")] : null, null) : null, Disabled: disabled);
    static readonly Dictionary<int, DateTimeOffset> NoneRefused = [];
    static readonly Dictionary<(int, string), DateTimeOffset> NoScope = [];
    static AgentStop StopAt(DateTimeOffset at, DateTimeOffset? resets, params string[] runs) => new(new Limit("five_hour", resets, at), runs, "claude-fable-5-2");
    /// A live window's record: this test process, as Claude Code writes `sessions/<pid>.json`.
    void Live(string sid)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Directory.CreateDirectory(Path.Combine(_t.Path, "sessions"));
        File.WriteAllText(Path.Combine(_t.Path, "sessions", "1.json"), $$"""{"pid":{{self.Id}},"sessionId":"{{sid}}","procStart":"{{self.StartTime.ToUniversalTime().ToFileTimeUtc()}}"}""");
    }

    [Fact]
    public void TonightsWindowIsToldOnceItsLoginHasRoomAgain()
    {
        Tonight();
        Assert.Null(CarryOn.StopOf(Window(), DateTime.UtcNow)); // the agents' files were written this minute: not quiet yet
        var stop = Stop();
        Assert.NotNull(stop);
        Assert.Equal(new Limit("five_hour", Reset, Stop2), stop.Limit); // the newest of the two errors
        Assert.Equal(new[] { "wf_x" }, stop.Runs);
        Assert.Equal("claude-fable-5-2", stop.Model);
        var key = CarryOn.Key("sid", stop);
        var told = new Dictionary<string, DateTime>();

        // At the stop: the login is on #1, which refused the agents, and every other account is full too.
        var login = (Account: (int?)1, ChangedAt: Stop1.UtcDateTime.AddMinutes(-3));
        List<Account> full = [A(1, 100, active: true, resets: Reset), A(2, 100, resets: Now.AddMinutes(-1)), A(3, 100), A(4, 100)];
        Assert.Equal(1, ResumeStopped.Refuser(stop.Limit, null, full, login));
        var room = CarryOn.Room(full[0], stop, 1, movedSince: false, 95, Stop2.AddSeconds(30), new Dictionary<int, DateTimeOffset> { [1] = Reset }, NoScope);
        Assert.Equal("no room yet", CarryOn.Hold(autoSwitch: true, told, key, room));

        // A quarter of an hour later #2's window reset and the login moved there; Shiftbar typed /remote-control into the window, which is not anyone typing.
        Main([Asked(Reply.AddMinutes(-10)), Done(Reply), TurnTime(Reply.AddSeconds(3)), .. Reconnect(Stop2.AddMinutes(14))]);
        Assert.Equal(key, CarryOn.Key("sid", Stop()!));
        List<Account> moved = [A(1, 100, resets: Reset), A(2, 3, active: true, resets: Now.AddHours(5)), A(3, 100), A(4, 100)];
        login = (2, Stop2.UtcDateTime.AddMinutes(14));
        room = CarryOn.Room(moved[1], stop, ResumeStopped.Refuser(stop.Limit, null, moved, login), movedSince: true, 95, Now, new Dictionary<int, DateTimeOffset> { [1] = Reset }, NoScope);
        Assert.Null(CarryOn.Hold(autoSwitch: true, told, key, room)); // due: the line is typed
        var line = CarryOn.Line(stop, 200)!;
        Assert.Equal($"Shiftbar: a usage limit stopped this window's background agents at {Stop2.ToLocalTime():HH:mm} (workflow wf_x). The account has room again: carry on and resume the stopped work.", line);

        // Typed and entered: the window has a message again and runs, and this stop is never acted on twice.
        told[key] = Now.UtcDateTime;
        Assert.Equal(CarryOn.Told, CarryOn.Hold(true, told, key, room: true));
        Main([Asked(Reply.AddMinutes(-10)), Done(Reply), TurnTime(Reply.AddSeconds(3)), .. Reconnect(Stop2.AddMinutes(14)), Asked(Now, line)]);
        Assert.Null(Stop());
    }

    [Fact]
    public void AWindowThatIsWorkingIsNeverTold()
    {
        Tonight();
        Assert.NotNull(Stop());
        Assert.Null(Stop("waiting")); // a permission prompt is open
        Assert.Null(CarryOn.StopOf(Window(), DateTime.UtcNow.AddSeconds(100))); // something in the session was written under two minutes ago

        Main(Asked(Reply.AddMinutes(-10)), Done(Reply), Asked(Reply.AddMinutes(1), "And the rest."), Tool("main", Reply.AddMinutes(2)));
        Assert.Null(Stop()); // its main loop is in a tool call
        Tonight();

        Agent("wf_x", "a1", Tool("a1", Stop1), ToolResult("a1", Stop1.AddMinutes(8)), Tool("a1", Stop1.AddMinutes(9)));
        Assert.Null(Stop()); // one agent of the run is mid-tool: the run is alive
        Tonight();

        Journal("wf_y", Started("c1"));
        Agent("wf_y", "c1", Tool("c1", Stop2.AddMinutes(1)));
        Assert.Null(Stop()); // another run of the window still works
        File.Delete(Path.Combine(RunDir("wf_y"), "agent-c1.jsonl"));
        Assert.Null(Stop()); // an agent just started, its file not there yet
        Journal("wf_y", Started("c1"), Result("c1"));
        Assert.Null(Stop()); // between agents, or about to settle
        Settle("wf_y");
        Assert.NotNull(Stop()); // settled: only the stopped run is left

        Journal("wf_x", Started("a1"), Started("a2"), Started("../../../sid"));
        Assert.Null(Stop()); // an agent id that is no file name is never followed out of the run's folder
    }

    [Fact]
    public void AWindowWithAPlainSubagentStillWorkingIsNeverTold()
    {
        Tonight();
        var file = Plain("p1", Tool("p1", Stop2.AddMinutes(1)));
        Assert.Null(Stop()); // mid-tool (a long build): silent for minutes, and not stopped
        Plain("p1", Asked(Stop2.AddMinutes(1)));
        Assert.Null(Stop()); // just started, or waiting for the API
        Plain("p1", Tool("p1", Stop2.AddMinutes(1)), ToolResult("p1", Stop2.AddMinutes(2)), Done(Stop2.AddMinutes(3)));
        Assert.NotNull(Stop()); // it finished its reply
        Plain("p1", Tool("p1", Stop2.AddMinutes(1)));
        File.SetLastWriteTimeUtc(file, Later - CarryOn.Working);
        Assert.NotNull(Stop()); // killed mid-tool long ago: it does not hold the window for good

        // Plain subagents only: one the limit stopped, a sibling still at work.
        Directory.Delete(RunDir("wf_x"), true);
        Plain("b1", Dead("b1", Stop1));
        Assert.NotNull(Stop());
        Plain("p1", Tool("p1", Stop2));
        Assert.Null(Stop());
    }

    [Fact]
    public void ARunStillGoingUnderAnotherSessionIdOfTheFolderHoldsTheLine()
    {
        Tonight();
        var old = Path.Combine(Project, "old-sid", "subagents", "workflows", "wf_o");
        Directory.CreateDirectory(old);
        var journal = Path.Combine(old, "journal.jsonl");
        File.WriteAllLines(journal, [Started("o1")]);
        Assert.Null(Stop()); // the window ran /clear while wf_o was going: it still writes under the old id
        Live("old-sid");
        Assert.NotNull(Stop()); // another live window's run is not this one's
        Directory.Delete(Path.Combine(_t.Path, "sessions"), true);
        File.SetLastWriteTimeUtc(journal, Later - CarryOn.Working);
        Assert.NotNull(Stop()); // nothing written there for long: a dead window's run
        File.SetLastWriteTimeUtc(journal, DateTime.UtcNow);
        Directory.CreateDirectory(Path.Combine(Project, "old-sid", "workflows"));
        File.WriteAllText(Path.Combine(Project, "old-sid", "workflows", "wf_o.json"), "{}");
        Assert.NotNull(Stop()); // settled
    }

    [Fact]
    public void ARunResumedUnderItsOwnIdIsRunningAgainWhateverItsOldSnapshotSays()
    {
        // wf_x settled once (killed), then was resumed under the same id: the old snapshot stays while the journal goes on.
        Tonight();
        Settle("wf_x");
        File.SetLastWriteTimeUtc(Snapshot("wf_x"), Stop1.UtcDateTime.AddMinutes(-30));
        Assert.NotNull(Stop()); // its new agents ended at the limit: a stop, not a settled run
        Assert.Equal("wf_x", Assert.Single(ResumeStopped.RunningWorkflows(Project, Reply.UtcDateTime)).RunId);

        Agent("wf_x", "a1", Tool("a1", Stop1));
        Assert.Null(Stop()); // and one of them mid-tool: alive
        Settle("wf_x");
        Assert.Null(Stop()); // settled again: the snapshot is rewritten after the journal
        Assert.Empty(ResumeStopped.RunningWorkflows(Project, Reply.UtcDateTime));
    }

    [Fact]
    public void AWindowSomeoneTypedIntoSinceTheStopIsNeverTold()
    {
        string[] turn = [Asked(Reply.AddMinutes(-10)), Done(Reply), TurnTime(Reply.AddSeconds(3))];
        Tonight();
        Main([.. turn, Asked(Stop2.AddMinutes(2), "Resume.")]);
        Assert.Null(Stop()); // a message waiting for its reply
        Main([.. turn, Asked(Stop2.AddMinutes(2), "How is it going?"), Done(Stop2.AddMinutes(3))]);
        Assert.Null(Stop()); // asked and answered after the stop: the window moved on, whatever it said
        Main([.. turn, .. Command("usage", Stop2.AddMinutes(2))]);
        Assert.Null(Stop()); // a slash command typed since: someone is at that window
        Main([.. turn, """{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>"}}"""]);
        Assert.Null(Stop()); // the same as older versions wrote it, and unstamped: counted as just now
        Main([.. turn, .. Command("usage", Stop1.AddMinutes(2))]);
        Assert.Null(Stop()); // typed after the first agent stopped and before the last one did
        Main([.. turn, .. Command("usage", Stop1.AddMinutes(-5))]);
        Assert.NotNull(Stop()); // a look before the agents stopped is not since the stop
        Main([.. turn, .. Reconnect(Stop2.AddMinutes(14))]);
        Assert.NotNull(Stop()); // Shiftbar's own /remote-control
    }

    [Fact]
    public void AStopTheRunMovedOnFromIsNotOne()
    {
        Tonight();
        Agent("wf_x", "a2", [.. Dead("a2", Stop2), Request("a2", Stop2.AddMinutes(20)), Tool("a2", Stop2.AddMinutes(20))]);
        Assert.Null(Stop()); // the agent tried again after the reset and went on
        Tonight();

        Journal("wf_x", Started("a1"), Started("a2"), Result("a2"), Started("a3"));
        Agent("wf_x", "a3", Tool("a3", Stop2.AddMinutes(1)));
        Assert.Null(Stop()); // the run took a2's failure as its result and started the next agent
        Agent("wf_x", "a3", Dead("a3", Stop2.AddMinutes(4)));
        Assert.Equal(Stop2.AddMinutes(4), Stop()!.Limit.At); // which the limit stopped too: a1 and a3 are what is open

        // After the reset the run starts the refused steps again by itself, under new agent ids: the old ones are closed.
        Journal("wf_x", Started("a1"), Started("a2"), Started("a4", step: "a1"), Result("a4", step: "a1"), Started("a5", step: "a2"));
        Agent("wf_x", "a5", Tool("a5", Stop2.AddMinutes(30)));
        Assert.Null(Stop()); // a2's step is at work again
        Journal("wf_x", Started("a1"), Started("a2"), Started("a4", step: "a1"), Result("a4", step: "a1"), Started("a5", step: "a2"), Result("a5", step: "a2"));
        Assert.Null(Stop()); // both came back: between agents, though a1 and a2 never got a line again

        Settle("wf_x");
        Assert.Null(Stop()); // settled (the window's user stopped or restarted it)

        Tonight();
        File.Delete(Snapshot("wf_x"));
        Assert.NotNull(Stop());
        Assert.Null(CarryOn.StopOf(Window(started: Stop2.UtcDateTime.AddMinutes(5)), Later)); // a session resumed in a new process: the old stop is not its own
        Assert.Null(CarryOn.StopOf(Window(started: DateTime.UtcNow.AddMinutes(1)), Later)); // nor is a run whose journal is older than the window
    }

    [Fact]
    public void SeveralStoppedRunsAreOneStop()
    {
        Tonight();
        Journal("wf_z", Started("d1"));
        Agent("wf_z", "d1", Dead("d1", Stop2.AddMinutes(2)));
        var stop = Stop()!;
        Assert.Equal(Stop2.AddMinutes(2), stop.Limit.At);
        Assert.Equal(new[] { "wf_z", "wf_x" }, stop.Runs); // newest first
        Assert.Contains("(workflow wf_z and 1 more)", CarryOn.Line(stop, 200));
    }

    [Fact]
    public void APlainSubagentStoppedAtALimitCountsToo()
    {
        Main(Asked(Reply.AddMinutes(-10)), Done(Reply));
        var agents = Path.Combine(Project, "sid", "subagents");
        Directory.CreateDirectory(agents);
        var file = Path.Combine(agents, "agent-b1.jsonl");
        File.WriteAllLines(file, Dead("b1", Stop1));
        var stop = Stop("idle")!;
        Assert.Equal(Stop1, stop.Limit.At);
        Assert.Empty(stop.Runs);
        Assert.DoesNotContain("workflow", CarryOn.Line(stop, 200));

        File.SetLastWriteTimeUtc(file, Reply.UtcDateTime.AddMinutes(-1));
        Assert.Null(Stop("idle")); // a subagent that ended before the main loop last spoke is not read
        File.WriteAllLines(file, [.. Dead("b1", Stop1), Tool("b1", Stop1.AddMinutes(1))]);
        Assert.Null(Stop("idle")); // it went on after the error
    }

    [Fact]
    public void AWindowWhoseOwnTranscriptEndsAtALimitIsNotThisCase()
    {
        Tonight();
        Main(Asked(Reply.AddMinutes(-10)), Done(Reply), Asked(Stop2.AddMinutes(1), "<task-notification>"), Refused("main", Stop2.AddMinutes(1)));
        Assert.Null(Stop()); // its own stop: the notice and "Continue on" have it
    }

    [Fact]
    public void RoomIsAnotherAccountOrAPassedResetAndNeverARefusedOrUnreadOne()
    {
        var stop = StopAt(Stop2, Reset);
        bool Room(Account? on, int? refuser, bool moved = false, DateTimeOffset? now = null, Dictionary<int, DateTimeOffset>? refused = null, Dictionary<(int, string), DateTimeOffset>? scoped = null) =>
            CarryOn.Room(on, stop, refuser, moved, 95, now ?? Now, refused ?? NoneRefused, scoped ?? NoScope);

        Assert.True(Room(A(2, 3), refuser: 1)); // another account than the one that refused, with room
        Assert.False(Room(A(1, 3), refuser: 1)); // the refuser itself, its reset ahead, whatever a stale reading says
        Assert.False(Room(A(1, 3), refuser: 1, now: Reset.AddMinutes(2))); // just past its reset: Claude Code restarts a parked run by itself then
        Assert.True(Room(A(1, 3), refuser: 1, now: Reset + CarryOn.SelfRetry)); // well past it and still stopped, with no switch at all
        Assert.False(Room(A(1, 100, resets: Reset), refuser: 1, now: Reset + CarryOn.SelfRetry)); // passed, but the reading is not fresh yet
        Assert.False(Room(A(2, 96), refuser: 1)); // at its switch line: the login is about to leave it
        Assert.False(Room(A(2, 3), refuser: 1, refused: new() { [2] = Now.AddHours(1) })); // the API refused it since, until a reset ahead
        Assert.True(Room(A(2, 3), refuser: 1, refused: new() { [2] = Now.AddHours(-1) }));
        Assert.False(Room(A(2, 3), refuser: 1, scoped: new() { [(2, "Fable")] = Now.AddHours(1) })); // the agents' model is out there
        Assert.True(Room(A(2, 3), refuser: 1, scoped: new() { [(2, "Opus")] = Now.AddHours(1) }));
        Assert.False(Room(A(2, 3, status: "unavailable"), refuser: 1)); // no live reading
        Assert.False(Room(A(2, 3, disabled: true), refuser: 1));
        Assert.False(Room(null, refuser: 1)); // the window's account is not known
        Assert.False(Room(A(2, 3), refuser: null)); // who refused is not known, and the login has not moved since
        Assert.True(Room(A(2, 3), refuser: null, moved: true)); // not known, but the login moved after the stop
        Assert.False(CarryOn.Room(A(1, 3), StopAt(Stop2, null), 1, false, 95, Now.AddDays(1), NoneRefused, NoScope)); // a refusal with no reset time never passes

        // An agent refused on its first request names no model: every model's window and refusal counts.
        var unknown = stop with { Model = null };
        Assert.False(CarryOn.Room(A(2, 3, fable: 100), unknown, 1, false, 95, Now, NoneRefused, NoScope));
        Assert.False(CarryOn.Room(A(2, 3), unknown, 1, false, 95, Now, NoneRefused, new Dictionary<(int, string), DateTimeOffset> { [(2, "Opus")] = Now.AddHours(1) }));
        Assert.True(CarryOn.Room(A(2, 3, fable: 40), unknown, 1, false, 95, Now, NoneRefused, new Dictionary<(int, string), DateTimeOffset> { [(2, "Opus")] = Now.AddHours(-1) }));
    }

    [Fact]
    public void OnlyWithAutoSwitchOnAndOncePerStopAcrossARestart()
    {
        var key = CarryOn.Key("sid", StopAt(Stop2, Reset));
        var path = Path.Combine(_t.Path, "carried-on.json");
        Assert.Null(CarryOn.Hold(autoSwitch: true, ResumeStopped.LoadMoves(path), key, room: true));
        Assert.Equal("auto-switch off", CarryOn.Hold(autoSwitch: false, ResumeStopped.LoadMoves(path), key, room: true));
        Assert.Equal("no room yet", CarryOn.Hold(true, ResumeStopped.LoadMoves(path), key, room: false));

        // Acted on two days ago, then the tray restarted: the stop is read back from disk, and stays for as long as a window sits idle.
        ResumeStopped.SaveMoves(path, new() { [key] = Now.UtcDateTime.AddDays(-2) }, Now.UtcDateTime, CarryOn.Remembered);
        var told = ResumeStopped.LoadMoves(path);
        Assert.Equal(CarryOn.Told, CarryOn.Hold(true, told, key, room: true));
        Assert.Null(CarryOn.Hold(true, told, CarryOn.Key("sid", StopAt(Now, Reset)), room: true)); // the same window stopped again later: a new stop
        Assert.Null(CarryOn.Hold(true, told, CarryOn.Key("other", StopAt(Stop2, Reset)), room: true));
    }

    [Fact]
    public void AStopWhoseWorkWentOnInAnotherWindowIsLeftAlone()
    {
        var (stop, s) = (StopAt(Stop2, Reset), Window());
        Dictionary<string, DateTime> none = [];
        Assert.False(CarryOn.Elsewhere(none, [s, s with { Pid = 7, SessionId = "other" }], s, stop));
        Assert.True(CarryOn.Elsewhere(new Dictionary<string, DateTime> { ["sid#1"] = Stop2.UtcDateTime.AddMinutes(1) }, [s], s, stop)); // "Continue on #1" after the stop
        Assert.False(CarryOn.Elsewhere(new Dictionary<string, DateTime> { ["sid#1"] = Stop2.UtcDateTime.AddMinutes(-1), ["sid2#1"] = Now.UtcDateTime }, [s], s, stop)); // reopened before it; another session
        Assert.True(CarryOn.Elsewhere(none, [s, s with { Pid = 7, ConfigDir = "elsewhere" }], s, stop)); // a second live claude holds the session
    }

    [Fact]
    public void TheLineWaitsWhileTheLoginIsJustMoving()
    {
        var now = Now.UtcDateTime;
        Assert.False(CarryOn.Settling(seen: 2, active: 2, now.AddMinutes(-5), now));
        Assert.True(CarryOn.Settling(seen: 1, active: 2, now.AddMinutes(-5), now)); // the Remote Control side has not seen the new login: nothing is queued yet
        Assert.True(CarryOn.Settling(seen: null, active: 2, now.AddMinutes(-5), now));
        Assert.True(CarryOn.Settling(seen: 2, active: 2, now.AddSeconds(-5), now)); // switched five seconds ago: the queue is being filled
        Assert.False(CarryOn.Settling(seen: 2, active: 2, now - RcReconnect.FirstWait, now));
        Assert.False(CarryOn.Settling(seen: 2, active: 2, DateTime.MaxValue, now)); // the login's record is unreadable: nothing to wait for
    }

    [Fact]
    public void TheHelpersAnswerEndsAStopOrTriesItAgain()
    {
        Assert.Equal((true, 0, 0), Short(CarryOn.After("sent", 0, 0)));
        Assert.Equal((true, 0, 3), Short(CarryOn.After("left mismatch", 0, 3)));
        Assert.Equal((false, 1, 0), Short(CarryOn.After("aborted typing", 0, 0))); // typed and taken out again: once more
        Assert.Equal((true, 2, 0), Short(CarryOn.After("aborted no-stop", 1, 0))); // twice: done with
        foreach (var answer in new[] { "refused not-empty", "error attach", "" })
        {
            var after = CarryOn.After(answer, 1, 0);
            Assert.Equal((false, 1, 1), Short(after)); // typed nothing: never the end of a stop
            Assert.Equal((TimeSpan.FromMinutes(1), false), (after.Wait, after.Tell));
        }
        // Half an hour of refusals: the owner is told, once, and it is tried every ten minutes from then on.
        Assert.Equal((false, TimeSpan.FromMinutes(1), false), Slow(CarryOn.After("refused not-empty", 0, CarryOn.Patience - 2)));
        Assert.Equal((false, TimeSpan.FromMinutes(10), true), Slow(CarryOn.After("refused not-empty", 0, CarryOn.Patience - 1)));
        Assert.Equal((false, TimeSpan.FromMinutes(10), false), Slow(CarryOn.After("refused not-empty", 0, CarryOn.Patience)));
        Assert.Equal((true, TimeSpan.FromMinutes(10), false), Slow(CarryOn.After("sent", 0, CarryOn.Patience))); // the box cleared: typed after all

        static (bool, int, int) Short((bool Done, int Tries, int Refusals, TimeSpan Wait, bool Tell) a) => (a.Done, a.Tries, a.Refusals);
        static (bool, TimeSpan, bool) Slow((bool Done, int Tries, int Refusals, TimeSpan Wait, bool Tell) a) => (a.Done, a.Wait, a.Tell);
    }

    [Fact]
    public void TheLineSaysWhoSpeaksAndFitsOneRowOrIsNotTyped()
    {
        var stop = StopAt(Stop2, Reset, "wf_deadbeef-123");
        var at = Stop2.ToLocalTime().ToString("HH:mm");
        var full = CarryOn.Line(stop, 400)!;
        Assert.Equal($"Shiftbar: a usage limit stopped this window's background agents at {at} (workflow wf_deadbeef-123). The account has room again: carry on and resume the stopped work.", full);
        Assert.Equal($"Shiftbar: a usage limit stopped this window's background agents at {at} (workflow wf_deadbeef-123). There is room again: resume them.", CarryOn.Line(stop, full.Length - 1));
        Assert.Equal($"Shiftbar: a usage limit stopped this window's background agents at {at}. There is room again: resume them.", CarryOn.Line(stop, 120 - 8)); // a 120-column window
        Assert.Equal("Shiftbar: a usage limit stopped your background agents. Resume them.", CarryOn.Line(stop, 80));
        Assert.Null(CarryOn.Line(stop, 60)); // a window too narrow for any of them gets nothing
        for (var width = 60; width <= 400; width++)
            if (CarryOn.Line(stop, width) is { } line)
            {
                Assert.True(line.Length <= width);
                Assert.StartsWith("Shiftbar: ", line);
                Assert.All(line, c => Assert.InRange(c, ' ', '~')); // keys a console can be sent and the box read back
            }
        // A folder name that is not a run id Claude Code makes is never typed.
        Assert.DoesNotContain("wf_", CarryOn.Line(StopAt(Stop2, Reset, "wf_x; rm -rf /"), 400));
    }

    [Fact]
    public void TheLogLineSaysWhatWasDoneAndNamesNoAddress()
    {
        var stop = StopAt(new DateTimeOffset(2026, 11, 3, 12, 0, 5, TimeSpan.Zero), Reset, "wf_x", "wf_y");
        var line = CarryOn.Said("sid", stop, A(2, 3).Number, "sent");
        Assert.Equal("carry on: sid sent (agents stopped at a usage limit 2026-11-03 12:00:05Z, wf_x wf_y; room on #2)", line);
        Assert.DoesNotContain("@", line);
    }

    [Fact]
    public void TheJournalsOpenAgentsAreThoseStartedAndNotReturned()
    {
        Assert.Equal(new[] { "a1", "a3" }, CarryOn.Open(["""{"type":"launched"}""", Started("a1"), Started("a2"), Result("a2"), Started("a3"), "{\"type\":\"result\",\"agentId\":\"a1"]));
        Assert.Equal(new[] { "a2" }, CarryOn.Open([Started("a2"), Result("a2"), Started("a2")])); // started again after it returned
        Assert.Empty(CarryOn.Open(["""{"type":"launched"}"""]));
        Assert.Equal(new[] { "a2", "a9" }, CarryOn.Open([Started("a1"), Started("a2"), Started("a9", step: "a1")])); // a1's step started again under a new id
    }
}
