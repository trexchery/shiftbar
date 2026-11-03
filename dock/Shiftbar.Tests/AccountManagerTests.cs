using System.Diagnostics;
using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

// All accounts in this file are synthetic.

public class SummaryTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    static Account A(int n, double fiveHour, double weekly, bool active = false, double fiveHourResetH = 2, double weeklyResetH = 60,
        bool disabled = false, string status = "ok", string? alias = null, Window? sevenDay = null) =>
        new(n, $"user{n}@example.com", active, status,
            new Usage(new Window(fiveHour, At(fiveHourResetH)), sevenDay ?? new Window(weekly, At(weeklyResetH)), null, null),
            Alias: alias, Disabled: disabled);

    /// Whole seconds: a fractional hour must not land a tick short of the minute it names.
    static DateTimeOffset At(double hours) => Now.AddSeconds(Math.Round(hours * 3600));

    [Fact]
    public void RoomMeansBothWindowsBelowTheLine()
    {
        Assert.True(Summary.HasRoom(A(1, 40, 60), 90));
        Assert.False(Summary.HasRoom(A(1, 95, 60), 90));
        Assert.False(Summary.HasRoom(A(1, 40, 90), 90)); // at the line is no room
        Assert.False(Summary.HasRoom(A(1, 10, 10, disabled: true), 90)); // excluded accounts are never "room"
        Assert.False(Summary.HasRoom(new Account(1, "x@example.com", false, "unavailable", null), 90)); // unknown is not room
    }

    [Fact]
    public void HeaderCountsAccountsWithRoomAndTheNextReset()
    {
        var accounts = new List<Account> { A(1, 20, 30, active: true, fiveHourResetH: 3), A(2, 50, 10, fiveHourResetH: 1.2), A(3, 98, 40, fiveHourResetH: 0.5), A(4, 10, 99, weeklyResetH: 30) };
        // Limited: #3 frees at its 5-hour reset (30 min), #4 at its weekly one (30 h): the soonest is #3's.
        Assert.Equal(Now.AddHours(0.5), Summary.NextReset(accounts, 95, Now));
        Assert.Equal("2 of 4 accounts have room · next reset in 30m", Summary.Header(new Snapshot(1, 1, accounts), 95, Now));
        // No account at the line: the soonest 5-hour reset.
        var roomy = accounts.Take(2).ToList();
        Assert.Equal(At(1.2), Summary.NextReset(roomy, 95, Now));
        Assert.Equal("2 of 2 accounts have room · next reset in 1h 12m", Summary.Header(new Snapshot(1, 1, roomy), 95, Now));
        Assert.Equal("", Summary.Header(null, 95, Now));
        Assert.Equal("", Summary.Header(new Snapshot(1, null, []), 95, Now));
    }

    [Fact]
    public void AnAccountFullOnBothWindowsFreesWhenTheLaterOneResets()
    {
        var both = A(1, 99, 99, fiveHourResetH: 1, weeklyResetH: 20);
        Assert.Equal(Now.AddHours(20), Summary.NextReset([both], 95, Now));
        Assert.Equal("1 account, at its limit · next reset in 20h 00m", Summary.Header(new Snapshot(1, 1, [both]), 95, Now));
    }

    [Fact]
    public void BestIsStrictlyBetterThanTheActiveAccount()
    {
        var active = A(1, 70, 40, active: true);
        Assert.Equal(3, Summary.Best([active, A(2, 50, 60), A(3, 20, 30)])?.Number);
        Assert.Null(Summary.Best([active, A(2, 70, 10)])); // a tie stays put, as the engine does
        Assert.Null(Summary.Best([active, A(2, 5, 5, disabled: true)])); // excluded
        Assert.Null(Summary.Best([active, new Account(2, "b@example.com", false, "unavailable", null)])); // unreadable
        Assert.Null(Summary.Best([A(1, 10, 10), A(2, 5, 5)])); // no active account
    }

    [Fact]
    public void PaceSaysOnPaceAheadOrWhenTheWeekRunsOut()
    {
        Window Wk(bool? will, bool? ahead, double? etaH = null) => new(60, Now.AddDays(3), AheadOfPace: ahead, WillLastToReset: will,
            ProjectedExhaustionAt: etaH is { } h ? Now.AddHours(h) : null);
        Assert.Equal(("weekly on pace", false), Summary.Pace(A(1, 0, 0, sevenDay: Wk(true, false)), Now));
        Assert.Equal(("weekly ahead of pace, lasts to reset", false), Summary.Pace(A(1, 0, 0, sevenDay: Wk(true, true)), Now));
        Assert.Equal(($"weekly runs out {Format.When(Now.AddHours(30), Now)}", true), Summary.Pace(A(1, 0, 0, sevenDay: Wk(false, true, 30)), Now));
        Assert.Equal(("weekly runs out in 5h 00m", true), Summary.Pace(A(1, 0, 0, sevenDay: Wk(false, true, 5)), Now));
        Assert.Equal(("weekly ahead of pace", false), Summary.Pace(A(1, 0, 0, sevenDay: Wk(null, true)), Now));
        Assert.Null(Summary.Pace(A(1, 0, 0, sevenDay: Wk(null, null)), Now)); // no pace from the engine: no line
        Assert.Null(Summary.Pace(new Account(1, "x@example.com", false, "ok", new Usage(null, null, null, null)), Now));
    }

    [Fact]
    public void PaceWarnsWhenTheWeekWillNotLastAndNoTimeIsAhead()
    {
        // Used up: the engine puts the exhaustion time at its reading, already past.
        Assert.Equal(("weekly used up", true), Summary.Pace(A(1, 0, 0, sevenDay: new(100, Now.AddDays(1), AheadOfPace: false, WillLastToReset: false, ProjectedExhaustionAt: Now.AddHours(-1))), Now));
        Assert.Equal(("weekly runs short", true), Summary.Pace(A(1, 0, 0, sevenDay: new(90, Now.AddDays(1), AheadOfPace: true, WillLastToReset: false, ProjectedExhaustionAt: Now.AddHours(-1))), Now));
    }

    [Fact]
    public void AnAccountWithNoReadingIsUnknownNotAtItsLimit()
    {
        var unread = new Account(2, "b@example.com", false, "unavailable", null);
        Assert.Equal("1 account, usage unknown", Summary.Header(new Snapshot(1, 2, [unread with { Active = true }]), 95, Now));
        Assert.StartsWith("1 of 2 accounts have room · 1 unknown", Summary.Header(new Snapshot(1, 1, [A(1, 10, 10, active: true), unread]), 95, Now));
        Assert.StartsWith("1 of 2 have room · 1 unknown", Summary.Tooltip(new Snapshot(1, 1, [A(1, 10, 10, active: true), unread]), auto: false));
    }

    [Fact]
    public void TooltipListsEveryAccountWithinWindowsLimit()
    {
        var snap = new Snapshot(1, 2, [A(1, 34, 58), A(2, 92, 71, active: true, alias: "work"), A(3, 5, 12)]);
        Assert.Equal("2 of 3 have room · auto-switch\n● work 92% · 71%\nuser1 34% · 58%\nuser3 5% · 12%", Summary.Tooltip(snap, auto: true, line: 90));
        Assert.Equal("Shiftbar — no accounts yet", Summary.Tooltip(null, false));

        var many = new Snapshot(1, 1, [.. Enumerable.Range(1, 12).Select(n => new Account(n, new string('x', 40) + n + "@example.com", n == 1, "ok",
            new Usage(new Window(10, null), new Window(20, null), null, null)))]);
        var tip = Summary.Tooltip(many, auto: true);
        Assert.True(tip.Length <= 127, $"{tip.Length}: {tip}");
        Assert.StartsWith("12 of 12 have room", tip);
        Assert.Matches(@"\+\d+ more$", tip);
        Assert.Contains("● ", tip); // the active account is listed first, so never cut
    }

    [Fact]
    public void NearLimitOffersASwitchOnlyForTheActiveAccountWithAutoSwitchOff()
    {
        var accounts = new List<Account> { A(1, 85, 40, active: true), A(2, 10, 10) };
        var near = new List<QuotaAlert> { new("work", "5-hour", QuotaState.Near, 85, Active: true) };
        Assert.Equal(2, Summary.SwitchOffer(near, accounts, autoSwitch: false)?.Number);
        Assert.Null(Summary.SwitchOffer(near, accounts, autoSwitch: true));
        Assert.Null(Summary.SwitchOffer([new("side", "5-hour", QuotaState.Near, 85)], accounts, autoSwitch: false)); // another account's
        Assert.Null(Summary.SwitchOffer([near[0] with { State = QuotaState.Normal }], accounts, autoSwitch: false)); // a reset is no offer
    }

    [Fact]
    public void QuotaAlertsMarkTheActiveAccount()
    {
        var q = new QuotaAlerts();
        Account Acct(double p, bool active) => new(1, "a@example.com", active, "ok", new Usage(new Window(p, null), null, null, null));
        q.Observe([Acct(10, true)]);
        Assert.True(Assert.Single(q.Observe([Acct(85, true)])).Active);
    }

    [Fact]
    public void SwitchedTextKeepsTheEngineWarnings()
    {
        // The warning first: the toast shows about three lines, and the lead line must never push it out of view.
        var text = Summary.SwitchedText("work", ["Account-2 is in use by a pinned terminal"]);
        Assert.StartsWith("Account-2 is in use by a pinned terminal\n", text);
        var lead = Summary.SwitchedText("work", null);
        Assert.DoesNotContain("\n", lead);
        Assert.True(lead.Length <= 80, lead);
    }

    [Fact]
    public void ParsesWeeklyPaceFieldsFromTheEngine()
    {
        var snap = Json.Parse<Snapshot>("""
        {"schemaVersion":1,"activeAccountNumber":1,"accounts":[{"number":1,"email":"a@example.com","active":true,"usageStatus":"ok",
          "usage":{"sevenDay":{"pct":70,"resetsAt":"2026-06-26T18:00:00Z","expectedPct":40,"aheadOfPace":true,
            "projectedExhaustionAt":"2026-06-25T15:40:00Z","willLastToReset":false}}}]}
        """);
        var w = snap.Accounts[0].Usage!.SevenDay!;
        Assert.False(w.WillLastToReset);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 15, 40, 0, TimeSpan.Zero), w.ProjectedExhaustionAt);
    }
}

public class SessionsTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    static LiveSession L(string id, string status = "busy", double ageH = 1, int pid = 10) =>
        new(pid, id, @"C:\work\app", @"C:\cfg", 2, status, Now.UtcDateTime.AddHours(-ageH));

    static StoppedSession Stop(string id, int pid = 10, DateTimeOffset? resets = null, bool orphan = false) =>
        new(pid, id, @"C:\work\app", @"C:\cfg", 2, "t.jsonl", AtLimit: !orphan, Limit: orphan ? null : new Limit("five_hour", resets, Now.AddMinutes(-1)), Orphan: orphan);

    [Fact]
    public void StateNamesWhatTheWindowIsDoing()
    {
        Assert.Equal("running", Sessions.State(L("a"), Now));
        Assert.Equal("running", Sessions.State(L("a", "shell"), Now));
        Assert.Equal("idle", Sessions.State(L("a", "idle"), Now));
        Assert.Equal("waiting for you", Sessions.State(L("a", "waiting"), Now));
        Assert.Equal("open", Sessions.State(L("a", ""), Now));
        Assert.Equal("stopped at limit · resets in 1h 12m", Sessions.State(L("a", "idle") with { Stop = Stop("a", resets: Now.AddMinutes(72)) }, Now));
        Assert.Equal("stopped at limit", Sessions.State(L("a", "idle") with { Stop = Stop("a") }, Now));
        Assert.Equal("terminal closed", Sessions.State(L("a") with { Stop = Stop("a", orphan: true) }, Now));
    }

    [Fact]
    public void AgeIsCompact()
    {
        Assert.Equal("just now", Sessions.Age(TimeSpan.FromSeconds(20)));
        Assert.Equal("12m", Sessions.Age(TimeSpan.FromMinutes(12.7)));
        Assert.Equal("3h", Sessions.Age(TimeSpan.FromHours(3.9)));
        Assert.Equal("2d", Sessions.Age(TimeSpan.FromDays(2.2)));
    }

    [Fact]
    public void ShortFolderKeepsTheEndOfALongPath()
    {
        Assert.Equal(@"~\code\app", Sessions.ShortFolder(@"C:\Users\pat\code\app\", @"C:\Users\pat"));
        Assert.Equal(@"C:\Users\patty", Sessions.ShortFolder(@"C:\Users\patty", @"C:\Users\pat")); // a sibling is not home
        Assert.Equal(@"…\clients\big-project", Sessions.ShortFolder(@"D:\very\deep\tree\of\folders\clients\big-project"));
        Assert.Equal(@"…\a-long-leaf-folder", Sessions.ShortFolder(@"D:\parent-folder-name\a-long-leaf-folder"));
        Assert.Equal("an-extremely-long-folder-na…", Sessions.ShortFolder(@"D:\an-extremely-long-folder-name-indeed"));
        Assert.Equal("/home/pat/x", Sessions.ShortFolder("/home/pat/x"));
    }

    [Fact]
    public void MergeLaysStopsOverTheirOwnWindowAndOrderPutsStopsFirst()
    {
        var live = new[] { L("old", ageH: 5, pid: 1), L("new", ageH: 0.1, pid: 2), L("stopped", "idle", ageH: 3, pid: 3) };
        var merged = Sessions.Merge(live, [Stop("stopped", pid: 3), Stop("new", pid: 99)]); // pid 99: another process of that session
        Assert.Null(merged.Single(s => s.SessionId == "new").Stop);
        Assert.True(merged.Single(s => s.SessionId == "stopped").AtLimit);
        Assert.Equal(["stopped", "new", "old"], Sessions.Order(merged).Select(s => s.SessionId).ToList());
    }

    [Fact]
    public void LiveSessionsListsOnlyLiveInteractiveWindows()
    {
        using var dir = new TempDir();
        var records = Path.Combine(dir.Path, "sessions");
        Directory.CreateDirectory(records);
        using var self = Process.GetCurrentProcess();
        var stamp = self.StartTime.ToUniversalTime().ToFileTimeUtc();
        File.WriteAllText(Path.Combine(records, "1.json"),
            $$"""{"pid":{{self.Id}},"sessionId":"live","cwd":"C:\\w","status":"busy","kind":"interactive","procStart":"{{stamp}}"}""");
        File.WriteAllText(Path.Combine(records, "2.json"),
            $$"""{"pid":{{self.Id}},"sessionId":"headless","cwd":"C:\\w","status":"busy","entrypoint":"sdk-cli","procStart":"{{stamp}}"}""");
        File.WriteAllText(Path.Combine(records, "3.json"), """{"pid":2147483000,"sessionId":"dead","cwd":"C:\\w","status":"idle","procStart":"1"}""");
        File.WriteAllText(Path.Combine(records, "4.json"), "{ half written");
        var found = Assert.Single(ResumeStopped.LiveSessions([(dir.Path, 4)]));
        Assert.Equal(("live", @"C:\w", 4, "busy"), (found.SessionId, found.Cwd, found.Account, found.Status));
        Assert.Equal(DateTime.FromFileTimeUtc(stamp), found.StartedUtc);
    }
}

public class ContinueOnTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    static Account A(int n, double peak, bool active = false, bool disabled = false) =>
        new(n, $"user{n}@example.com", active, "ok", new Usage(new Window(peak, Now.AddHours(2)), new Window(peak / 2, Now.AddDays(3)), null, null), Disabled: disabled);

    static StoppedSession Stop(int? account, string status = "idle") =>
        new(1, "sid", @"C:\work\app", @"C:\cfg", account, "t.jsonl", AtLimit: true, Status: status,
            Limit: new Limit("five_hour", Now.AddHours(1), Now.AddMinutes(-1)), OwnRefused: true);

    [Fact]
    public void ContinuesOnTheAccountWithMostRoomButNeverItsOwn()
    {
        var accounts = new List<Account> { A(1, 30, active: true), A(2, 100), A(3, 10), A(4, 5, disabled: true) };
        Assert.Equal(3, ContinueOn.Target(Stop(2), accounts, Dir, Free, Now));
        // Its own account has room again: still another one, since a second window on its transcript would collide.
        Assert.Equal(1, ContinueOn.Target(Stop(3), accounts, Dir, Free, Now));
        // Refused until its reset, whatever the reading says.
        Assert.Equal(1, ContinueOn.Target(Stop(2), accounts, Dir, Free, Now, refusedUntil: new Dictionary<int, DateTimeOffset> { [3] = Now.AddHours(1) }));
        Assert.Null(ContinueOn.Target(Stop(1), [A(1, 100, active: true), A(2, 100)], Dir, Free, Now)); // nowhere has room
    }

    static string Dir(int n) => $@"C:\profiles\{n}";
    static bool Free(string dir) => false;

    [Fact]
    public void NeverContinuesWhereTheStoppedWindowOrAnotherOnItsSessionLives()
    {
        var accounts = new List<Account> { A(1, 30, active: true), A(2, 100), A(3, 10) };
        // The stop's account was recorded as 2 (the login it had), but the login moved to 1, whose folder is the stop's own:
        // resuming there would run the session twice in one folder. 3 has a folder of its own.
        var inDefault = Stop(2) with { ConfigDir = @"C:\cfg" };
        Assert.Equal(3, ContinueOn.Target(inDefault, accounts, n => n == 1 ? @"C:\CFG\" : Dir(n), Free, Now));
        // A live claude on the session in 3's folder (a window continued there before): nowhere left.
        Assert.Null(ContinueOn.Target(inDefault, accounts, n => n == 1 ? @"C:\cfg" : Dir(n), d => d == Dir(3), Now));
        // An account no folder can be named for is no target.
        Assert.Equal(1, ContinueOn.Target(Stop(2), accounts, n => n == 3 ? null : Dir(n), Free, Now));
    }

    [Fact]
    public void ArgsResumeTheConversationThroughCswapRun()
    {
        Assert.Equal(["run", "3", "--restore-rc", "--", "--resume", "sid", ResumeStopped.Prompt], ContinueOn.Args(Stop(2), 3, restoreRc: true));
        Assert.Equal(["run", "1", "--", "--resume", "sid", ResumeStopped.Prompt + ResumeStopped.Background], ContinueOn.Args(Stop(2, "shell"), 1, restoreRc: false));
    }

    [Fact]
    public void TheActiveAccountResumesInTheDefaultLoginsFolder()
    {
        Assert.Equal(ResumeStopped.DefaultDir, ContinueOn.DirFor(1, [A(1, 10, active: true)]));
        Assert.Null(ContinueOn.DirFor(9, [A(1, 10, active: true)]));
    }

    [Fact]
    public void AnAccountWithoutAProfileResumesWhereCswapRunCreatesIt()
    {
        // As the engine's slugify_email: ASCII letters, digits and `._-` kept, anything else one `_` per character.
        var a = new Account(7, "jo+dev@exämple.com", false, "ok", null);
        Assert.Equal("7-jo_dev_ex_mple.com", Path.GetFileName(ResumeStopped.NewProfileDir(a)));
        Assert.Equal("sessions", Path.GetFileName(Path.GetDirectoryName(ResumeStopped.NewProfileDir(a))));
    }
}
