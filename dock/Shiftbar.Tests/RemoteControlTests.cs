using System.Diagnostics;
using System.Text.Json;
using Shiftbar;
using Xunit;
using static Shiftbar.ConsoleNudge;

namespace Shiftbar.Tests;

/// Synthetic transcripts, shaped like Claude Code 2.1.317's: `bridge-session` while Remote Control is connected, a
/// `bridge_status` system line when it (re)connects, nothing when it drops.
public class RemoteControlTests
{
    const string A = "37a66dc7-0000-4000-8000-00000000000a", B = "4b1c2d3e-0000-4000-8000-00000000000b", C = "5c2d3e4f-0000-4000-8000-00000000000c";
    static string Bridge(string owner, string bridge = "cse_01") =>
        $$"""{"type":"bridge-session","sessionId":"s1","bridgeSessionId":"{{bridge}}","lastSequenceNum":0,"ownerAccountUuid":"{{owner}}","ownerOrganizationUuid":"org-1"}""";
    const string Status = """{"type":"system","subtype":"bridge_status","content":"/remote-control is active · Continue here, on your phone, or at https://claude.ai/code/session_x"}""";
    const string Done = """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"All done."}]}}""";
    const string Typed = """{"type":"user","message":{"role":"user","content":"go on"}}""";

    [Fact]
    public void ASwitchDropsTheRemoteControlOfTheLoginItLeft()
    {
        var lines = new[] { Status, Bridge(A), Typed, Done, Bridge(A, "cse_01") };
        Assert.Equal((true, A), RemoteControl.Bridge(lines));
        Assert.True(RemoteControl.Dropped(RemoteControl.Bridge(lines), was: A, now: B));
        Assert.False(RemoteControl.Dropped(RemoteControl.Bridge(lines), was: null, now: B)); // the previous login unknown: no proof
    }

    [Fact]
    public void ARecordFromAnEarlierRunOfAResumedSessionDoesNotCount()
    {
        static string At(string ts) => $$"""{"type":"user","message":{"role":"user","content":"go on"},"timestamp":"{{ts}}"}""";
        var started = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        // Yesterday's run had RC; today's process (resumed without it) appended its own lines after.
        Assert.Equal((false, null), RemoteControl.Bridge([Bridge(A), At("2026-11-02T18:00:00.000Z"), At("2026-11-03T09:05:00.000Z")], started));
        // RC connected by today's process: only its own lines follow.
        Assert.Equal((true, A), RemoteControl.Bridge([At("2026-11-02T18:00:00.000Z"), Bridge(A), At("2026-11-03T09:05:00.000Z")], started));
        Assert.Equal((true, A), RemoteControl.Bridge([Bridge(A), At("2026-11-02T18:00:00.000Z")])); // no start time: as before
    }

    [Fact]
    public void OnlyARecordPastTheMarkConfirmsAReconnect()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "s1.jsonl");
        File.WriteAllText(file, Bridge(A) + "\n" + Done + "\n");
        var mark = new FileInfo(file).Length;
        Assert.False(RemoteControl.ConnectedSince(file, mark, A)); // switched back to A: A's old record is no reconnect
        File.AppendAllText(file, Typed + "\n");
        Assert.False(RemoteControl.ConnectedSince(file, mark, A));
        File.AppendAllText(file, Bridge(A, "cse_02") + "\n");
        Assert.True(RemoteControl.ConnectedSince(file, mark, A));
        Assert.False(RemoteControl.ConnectedSince(file, mark, B));
        Assert.False(RemoteControl.ConnectedSince(Path.Combine(t.Path, "missing.jsonl"), 0, A));
    }

    [Fact]
    public void AReconnectOnTheNewLoginIsNotDropped()
    {
        // The user typed /remote-control after the swap: a new bridge owned by the new login, then its status line.
        var lines = new[] { Bridge(A), Done, Bridge(B, "cse_02"), Status };
        Assert.Equal((true, B), RemoteControl.Bridge(lines));
        Assert.False(RemoteControl.Dropped(RemoteControl.Bridge(lines), was: A, now: B));
    }

    [Fact]
    public void NoRemoteControlNothingToRestore()
    {
        var lines = new[] { Typed, Done, """{"type":"system","subtype":"informational","content":"bridge-session mentioned in text"}""", "{\"type\":\"bridge-sess" };
        Assert.Equal((false, null), RemoteControl.Bridge(lines));
        Assert.False(RemoteControl.Dropped(RemoteControl.Bridge(lines), A, B));
    }

    [Fact]
    public void RemoteControlOfAnotherAccountIsNotThisSwitchs()
    {
        var bridge = RemoteControl.Bridge([Bridge(C), Done]);
        Assert.False(RemoteControl.Dropped(bridge, was: A, now: B)); // it had RC on C, not on the login this switch left
        Assert.False(RemoteControl.Dropped((true, null), A, B)); // an owner that cannot be read is not proof
    }

    [Fact]
    public void TheLoginUuidIsReadFromTheGlobalConfigOnly()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, ".claude.json");
        File.WriteAllText(file, $$$"""{"numStartups":3,"oauthAccount":{"accountUuid":"{{{B}}}","emailAddress":"b@example.com","organizationUuid":"org-1"} }""");
        Assert.Equal(B, RemoteControl.LoginUuid(file));
        File.WriteAllText(file, """{"numStartups":3}""");
        Assert.Null(RemoteControl.LoginUuid(file));
        Assert.Null(RemoteControl.LoginUuid(Path.Combine(t.Path, "missing.json")));
        File.WriteAllText(file, "{ half");
        Assert.Null(RemoteControl.LoginUuid(file));
    }

    static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheGateOpensOnlyForAnIdleFinishedQuietWindowAndAnAbsentUser()
    {
        var quiet = Now.AddSeconds(-11);
        var away = TimeSpan.FromSeconds(31);
        Assert.Null(RemoteControl.Gate(Now, quiet, endsTurn: true, "idle", away, manual: false));
        Assert.Equal("busy", RemoteControl.Gate(Now, quiet, true, "busy", away, false));
        Assert.Equal("busy", RemoteControl.Gate(Now, quiet, true, "waiting", away, false)); // a permission prompt
        Assert.Equal("busy", RemoteControl.Gate(Now, quiet, true, null, away, false));
        Assert.Equal("recent-activity", RemoteControl.Gate(Now, Now.AddSeconds(-9), true, "idle", away, false));
        Assert.Equal("mid-turn", RemoteControl.Gate(Now, quiet, endsTurn: false, "idle", away, false));
        Assert.Equal("user-active", RemoteControl.Gate(Now, quiet, true, "idle", TimeSpan.FromSeconds(29), false));
        Assert.Equal("user-active", RemoteControl.Gate(Now, quiet, true, "idle", null, false)); // unknown is not away
        Assert.Null(RemoteControl.Gate(Now, quiet, true, "idle", TimeSpan.Zero, manual: true)); // the toast's button: the user asked
        Assert.Equal("mid-turn", RemoteControl.Gate(Now, quiet, false, "idle", null, manual: true)); // but never mid-turn
    }

    [Fact]
    public void ContinueOnAddsRemoteControlWhenTheStoppedSessionHadIt()
    {
        using var t = new TempDir();
        var with = Path.Combine(t.Path, "with.jsonl");
        var without = Path.Combine(t.Path, "without.jsonl");
        File.WriteAllLines(with, [Bridge(A), Done]);
        File.WriteAllLines(without, [Typed, Done]);
        string[] resume = ["--resume", "s1", "A usage limit was hit. Continue."];
        Assert.Equal([.. resume, "--remote-control"], RemoteControl.WithRc(resume, with, on: true));
        Assert.Equal(resume, RemoteControl.WithRc(resume, without, on: true));
        Assert.Equal(resume, RemoteControl.WithRc(resume, with, on: false));
        Assert.Equal(["--resume", "s1", "--rc"], RemoteControl.WithRc(["--resume", "s1", "--rc"], with, on: true));
        var s = new StoppedSession(1, "s1", @"C:\w", @"C:\cfg", 1, with);
        Assert.Equal(["run", "2", "--restore-rc", "--", "--resume", "s1", ResumeStopped.Prompt, "--remote-control"], ContinueOn.Args(s, 2, restoreRc: true));
    }

    [Fact]
    public void OlderSettingsFilesReconnectRemoteControl()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(file, """{ "AutoSwitch": true, "PreserveRemoteControl": true }""");
        Assert.True(DockSettings.Load(file).ReconnectRemoteControl);
        new DockSettings { ReconnectRemoteControl = false }.Save(file);
        Assert.False(DockSettings.Load(file).ReconnectRemoteControl);
    }
}

/// The screen reader: synthetic dumps of a console's visible rows.
public class InputBoxTests
{
    const int W = 60;
    static string Rule(int w = W) => new('─', w);
    static string[] Screen(params string[] box) => ["● Done with that turn.", "", .. box, "", ""];

    [Fact]
    public void AnEmptyBoxIsEmpty()
    {
        var b = Parse(Screen(Rule(), "> ", Rule(), "  ? for shortcuts"), W);
        Assert.Equal(BoxState.Empty, b.State);
        Assert.Contains("0 chars after marker", b.Why);
        Assert.Equal(BoxState.Empty, Parse(Screen(Rule(), "❯", Rule(), "  ⏵⏵ accept edits on"), W).State);
    }

    [Fact]
    public void TextInTheBoxIsNotEmptyAndNeverShown()
    {
        var b = Parse(Screen(Rule(), "> fix the build please", Rule()), W);
        Assert.Equal(BoxState.Text, b.State);
        Assert.Equal(17, b.Chars); // non-blank characters
        Assert.DoesNotContain("build", b.Why); // the probe prints Why: shape only
        // Text on a continuation row counts too.
        Assert.Equal(BoxState.Text, Parse(Screen(Rule(), "> ", "  second line", Rule()), W).State);
    }

    [Fact]
    public void APlaceholderIsTextUnlessItsAttributesProveIt()
    {
        var row = "> Try \"refactor <filepath>\"".PadRight(W);
        var rows = Screen(Rule(), row, Rule());
        Assert.Equal(BoxState.Text, Parse(rows, W).State); // no attributes: no proof
        ushort[] Attrs(ushort text) => [.. Enumerable.Range(0, W).Select(i => i < 2 ? (ushort)0x07 : i == 2 ? (ushort)0x70 : text)];
        var dim = rows.Select((_, i) => i == 3 ? Attrs(0x08) : null).ToArray();
        var b = Parse(rows, W, dim);
        Assert.Equal(BoxState.Empty, b.State);
        Assert.Contains("placeholder", b.Why);
        var plain = rows.Select((_, i) => i == 3 ? Attrs(0x07) : null).ToArray();
        Assert.Equal(BoxState.Text, Parse(rows, W, plain).State); // typed text in the default colour
        Assert.Equal(BoxState.Text, Parse(rows, W, dim, typed: true).State); // after our typing nothing is a placeholder
    }

    [Fact]
    public void APermissionDialogOrMenuIsNeverAnEmptyBox()
    {
        // The dialog replaces the box: its selection line under a rule is text, never an empty prompt.
        Assert.NotEqual(BoxState.Empty, Parse(Screen(Rule(), " Bash command", "   rm -rf build", " Do you want to proceed?", "❯ 1. Yes", "  2. No", " Esc to cancel"), W).State);
        Assert.NotEqual(BoxState.Empty, Parse(Screen(Rule(), "❯ 1. Yes", "  2. No", Rule()), W).State);
        // An empty box drawn above a menu or a running turn's spinner is not trusted either.
        Assert.Equal(BoxState.Unrecognised, Parse(Screen(Rule(), "> ", Rule(), "❯ 1. Yes", "  2. No"), W).State);
        Assert.Equal(BoxState.Unrecognised, Parse(["✻ Thinking… (esc to interrupt)", "", Rule(), "> ", Rule()], W).State);
        Assert.Equal(BoxState.Unrecognised, Parse(Screen(Rule(), "> ", Rule(), "a", "b", "c", "d", "e", "f", "g"), W).State);
    }

    [Fact]
    public void AReplyEndingInAQuestionAboveTheBoxIsNoDialog()
    {
        string[] rows = ["● Do you want me to commit these changes?", "", Rule(), "> ", Rule(), "  ? for shortcuts"];
        Assert.Equal(BoxState.Empty, Parse(rows, W).State);
        Assert.Equal(BoxState.Unrecognised, Parse(Screen(Rule(), "> ", Rule(), " Do you want to proceed?"), W).State); // under it: a dialog
    }

    [Fact]
    public void ABoxAboveTheCursorIsTheLiveFrameAndOneBelowItIsNot()
    {
        var rows = Screen(Rule(), "> ", Rule(), "  ? for shortcuts"); // the box's top rule is row 2
        Assert.Equal(BoxState.Empty, Parse(rows, W, cursorRow: 3).State); // in the box
        Assert.Equal(BoxState.Empty, Parse(rows, W, cursorRow: rows.Length - 1).State); // under it
        Assert.Equal(BoxState.Unrecognised, Parse(rows, W, cursorRow: 0).State); // above it: an old frame
        Assert.Equal(BoxState.Unrecognised, Parse(rows, W, cursorRow: rows.Length).State); // off-screen: scrolled away from the live one
    }

    [Fact]
    public void AnUnrecognisedLayoutIsNotEmpty()
    {
        Assert.Equal(BoxState.Unrecognised, Parse(["PS C:\\> ", ""], W).State); // a shell, not Claude Code
        Assert.Equal(BoxState.Unrecognised, Parse(Screen("> ", Rule()), W).State); // no rule above the prompt
        Assert.Equal(BoxState.Unrecognised, Parse(Screen(Rule(), "> "), W).State); // no closing rule
        Assert.Equal(BoxState.Unrecognised, Parse(Screen(Rule(10), "> ", Rule(10)), W).State); // too short to be the box's rule
        Assert.Equal(BoxState.Unrecognised, Parse(Screen("╭" + Rule(W - 2) + "╮", "│ >  │", "╰" + Rule(W - 2) + "╯"), W).State); // the old rounded box
        Assert.Equal(BoxState.Unrecognised, Parse([], W).State);
        Assert.Contains("no rule followed by a prompt line", Parse(["x"], W).Why);
    }

    [Fact]
    public void ANarrowWindowStillReads()
    {
        Assert.Equal(BoxState.Empty, Parse(Screen(Rule(20), "> ", Rule(20)), 20).State);
        Assert.Equal(BoxState.Text, Parse(Screen(Rule(20), "> /remote-cont", "  rol", Rule(20)), 20).State);
    }

    [Fact]
    public void WideTextCountsAsText()
    {
        var cjk = Parse(Screen(Rule(), "> 修复构建", Rule()), W);
        Assert.Equal((BoxState.Text, 4), (cjk.State, cjk.Chars));
        var emoji = Parse(Screen(Rule(), "> 🚀", Rule()), W);
        Assert.Equal((BoxState.Text, 1), (emoji.State, emoji.Chars));
        Assert.Equal(BoxState.Unrecognised, Parse(Screen(Rule(30) + "🚀" + Rule(29), "> ", Rule()), W).State); // not a rule
    }

    [Fact]
    public void OurTypingReadsBackExactlyWithAutocompleteBelow()
    {
        var b = Parse(Screen(Rule(), "> /remote-control", Rule(), "  /remote-control   Connect this session to claude.ai", "  /resume", "  /review",
            "  /release-notes", "  /rename", "  /rewind", "  /remote-env"), W, typed: true);
        Assert.Equal(Command, b.Text);
        Assert.NotEqual(Command, Parse(Screen(Rule(), "> hi/remote-control", Rule()), W, typed: true).Text);
        Assert.NotEqual(Command, Parse(Screen(Rule(), "> /remote-control", "  more", Rule()), W, typed: true).Text);
    }

    [Fact]
    public void TheShapeHidesTheText() =>
        Assert.Equal("tRPRt", Shape(["secret words", Rule(), "> /secret", Rule(), "  ? for shortcuts", "", ""], W));
}

/// The reconnect queue: queued at a switch, typed when due, confirmed by a new bridge record, retried once, then given up.
public class RcReconnectTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    static RcReconnect.Entry E(int sw = 1, long mark = 0) => new("s1", 42, @"C:\cfg\projects\w\s1.jsonl", @"C:\work\shop", "new-uuid", sw, mark);

    [Fact]
    public void ConfirmedAfterTheFirstTry()
    {
        var q = new RcReconnect();
        var e = E();
        Assert.True(q.Queue(e, T0));
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, T0.AddSeconds(5), alive: true, confirmed: false)); // Claude Code gets a moment first
        Assert.Equal(RcReconnect.Step.Nudge, q.Next(e, T0 + RcReconnect.FirstWait, true, false));
        Assert.Equal(RcReconnect.Step.Wait, q.Report(e, "sent", T0.AddSeconds(20)));
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, T0.AddSeconds(30), true, false));
        Assert.Equal(RcReconnect.Step.Done, q.Next(e, T0.AddSeconds(31), true, confirmed: true));
        Assert.Empty(q.Entries);
        Assert.Equal("shop", e.Folder);
    }

    [Fact]
    public void RetriedOnceAfterTwoMinutesThenGivenUp()
    {
        var q = new RcReconnect();
        var e = E();
        q.Queue(e, T0);
        var t = T0 + RcReconnect.FirstWait;
        Assert.Equal(RcReconnect.Step.Nudge, q.Next(e, t, true, false));
        q.Report(e, "sent", t);
        t += RcReconnect.ConfirmWait;
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, t, true, false)); // unconfirmed: the retry is scheduled
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, t + RcReconnect.RetryWait - TimeSpan.FromSeconds(1), true, false));
        t += RcReconnect.RetryWait;
        Assert.Equal(RcReconnect.Step.Nudge, q.Next(e, t, true, false));
        q.Report(e, "sent", t);
        Assert.Equal(RcReconnect.Step.GiveUp, q.Next(e, t + RcReconnect.ConfirmWait, true, false));
        Assert.Empty(q.Entries);
    }

    [Fact]
    public void NeverQueuedTwiceForOneSwitch()
    {
        var q = new RcReconnect();
        Assert.True(q.Queue(E(1), T0));
        Assert.False(q.Queue(E(1), T0.AddMinutes(10)));
        var first = q.Entries.Single();
        var second = E(2);
        Assert.True(q.Queue(second, T0.AddMinutes(10))); // a later switch owes it again, replacing the first
        Assert.Equal(2, q.Entries.Single().Switch);
        Assert.False(q.Owes(first));
        q.Next(first, T0.AddMinutes(11), alive: true, confirmed: true); // the replaced one finishing forgets only itself
        Assert.True(q.Owes(second));
    }

    [Fact]
    public void ARefusalTypesNothingAndLooksAgainLater()
    {
        var q = new RcReconnect();
        var e = E();
        q.Queue(e, T0);
        var t = T0 + RcReconnect.FirstWait;
        Assert.Equal(RcReconnect.Step.Wait, q.Report(e, "refused not-empty", t));
        Assert.Equal(0, e.Tries);
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, t.AddSeconds(5), true, false));
        Assert.Equal(RcReconnect.Step.Nudge, q.Next(e, t + RcReconnect.Backoff, true, false));
        q.Later(e, t + RcReconnect.Backoff); // the tray's own gate said no
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, t + RcReconnect.Backoff, true, false));
        Assert.Equal(RcReconnect.Step.GiveUp, q.Next(e, T0 + RcReconnect.Patience, true, false)); // the user never left: the toast takes over
    }

    [Fact]
    public void TypedAndRemovedSpendsATry()
    {
        var q = new RcReconnect();
        var e = E();
        q.Queue(e, T0);
        q.Report(e, "aborted user-active", T0.AddSeconds(20));
        q.Report(e, "aborted recent-activity", T0.AddSeconds(40));
        Assert.Equal(RcReconnect.Step.GiveUp, q.Next(e, T0.AddMinutes(1), true, false));
    }

    [Fact]
    public void AnythingLeftInTheBoxStopsAtOnce()
    {
        var q = new RcReconnect();
        var e = E();
        q.Queue(e, T0);
        Assert.Equal(RcReconnect.Step.GiveUp, q.Report(e, "left mismatch", T0.AddSeconds(20)));
        Assert.Empty(q.Entries);
    }

    [Fact]
    public void AClosedWindowIsForgottenAndAReconnectByHandCounts()
    {
        var q = new RcReconnect();
        var e = E();
        q.Queue(e, T0);
        Assert.Equal(RcReconnect.Step.Drop, q.Next(e, T0.AddMinutes(1), alive: false, confirmed: false));
        var f = E(2);
        q.Queue(f, T0);
        Assert.Equal(RcReconnect.Step.Done, q.Next(f, T0.AddSeconds(1), true, confirmed: true)); // typed by the user, nothing to do
    }

    [Fact]
    public void AnOwedOrGivenUpWindowIsOwedAgainAtTheNextSwitchWithItsMark()
    {
        var q = new RcReconnect();
        var e = E(1, mark: 100);
        q.Queue(e, T0);
        Assert.Equal(100, q.Owed()["s1"]);
        Assert.Equal(RcReconnect.Step.GiveUp, q.Next(e, T0 + RcReconnect.Patience, true, false));
        Assert.Empty(q.Entries);
        Assert.Equal(100, q.Owed()["s1"]); // still without RC: a switch back owes it, past the same mark
        q.Queue(E(2, mark: 100), T0.AddHours(1));
        Assert.Equal(RcReconnect.Step.Done, q.Next(q.Entries.Single(), T0.AddHours(1), true, confirmed: true));
        Assert.Empty(q.Owed());
        var left = E(3);
        q.Queue(left, T0);
        q.Report(left, "left mismatch", T0.AddSeconds(20));
        Assert.Empty(q.Owed()); // something sits in that box: never typed into again
    }

    [Fact]
    public void TheToastsButtonIsOnlyConfirmed()
    {
        var q = new RcReconnect();
        var e = E();
        q.Track(e, T0);
        Assert.Equal(RcReconnect.Step.Wait, q.Next(e, T0.AddSeconds(30), true, false));
        Assert.Equal(RcReconnect.Step.GiveUp, q.Next(e, T0 + RcReconnect.ConfirmWait, true, false)); // no automatic retry after the user's own
    }
}

/// The helper against a real console: a hidden PowerShell child draws Claude Code's box ('─' rule, "> " prompt, rule) and
/// reads one line. The helper runs as its own process, as from the tray, against a synthetic session record for that child.
[Collection("own process")]
public class ConsoleNudgeIntegrationTests
{
    const string Fake = """
        param([string]$Out, [string]$Ready, [string]$Pre = '')
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $rule = [string]::new([char]0x2500, [Console]::WindowWidth - 1)
        [Console]::Clear()
        [Console]::WriteLine('Done with that turn.')
        [Console]::WriteLine('')
        [Console]::WriteLine($rule)
        $row = [Console]::CursorTop
        [Console]::WriteLine('> ' + $Pre)
        [Console]::WriteLine($rule)
        [Console]::WriteLine('  ? for shortcuts')
        [Console]::SetCursorPosition(2 + $Pre.Length, $row)
        [IO.File]::WriteAllText($Ready, 'ready')
        $line = [Console]::ReadLine()
        [IO.File]::WriteAllText($Out, $line)
        """;

    /// The tray's build of the helper: the copy beside the tests lacks its self-contained runtime.
    static string Helper => Path.Combine(AppContext.BaseDirectory.Replace(
        $"{Path.DirectorySeparatorChar}Shiftbar.Tests{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Shiftbar{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"), "shiftbar.exe");

    static async Task<(string Result, string? Received, string? Probe)> Run(string pre)
    {
        using var t = new TempDir();
        var script = Path.Combine(t.Path, "fake.ps1");
        File.WriteAllText(script, Fake);
        var (outFile, ready) = (Path.Combine(t.Path, "out.txt"), Path.Combine(t.Path, "ready.txt"));
        var child = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Out \"{outFile}\" -Ready \"{ready}\"" + (pre.Length > 0 ? $" -Pre \"{pre}\"" : ""))
        { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!; // its own console, hidden
        try
        {
            for (var i = 0; i < 200 && !File.Exists(ready); i++) await Task.Delay(100);
            Assert.True(File.Exists(ready), "the fake console never drew its box");
            // A synthetic default login for the helper: the child as an idle session whose last message is a finished reply.
            var home = Path.Combine(t.Path, "home");
            var claude = Path.Combine(home, ".claude");
            Directory.CreateDirectory(Path.Combine(claude, "sessions"));
            var cwd = Path.Combine(t.Path, "work");
            File.WriteAllText(Path.Combine(claude, "sessions", $"{child.Id}.json"), JsonSerializer.Serialize(new
            {
                pid = child.Id, sessionId = "s1", cwd, status = "idle", kind = "interactive",
                procStart = child.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(),
            }));
            var project = Path.Combine(claude, "projects", System.Text.RegularExpressions.Regex.Replace(cwd, "[^A-Za-z0-9]", "-"));
            Directory.CreateDirectory(project);
            var transcript = Path.Combine(project, "s1.jsonl");
            File.WriteAllText(transcript, """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"All done."}]}}""" + "\n");
            File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddMinutes(-1));
            // --manual: the person at this machine may be typing elsewhere; the hidden console's input is the child's alone.
            var result = await ConsoleNudge.RunAsync(Helper, child.Id, "s1", manual: true, new Dictionary<string, string> { ["USERPROFILE"] = home });
            for (var i = 0; i < 50 && result.StartsWith("sent") && !File.Exists(outFile); i++) await Task.Delay(100);
            // The box as it reads now: characters typed without Enter are echoed into it by the child's pending read.
            string? probe = null;
            if (!result.StartsWith("sent"))
            {
                using var p = Process.Start(new ProcessStartInfo(Helper) { ArgumentList = { "rc-nudge", "--probe", child.Id.ToString() }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                probe = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
            }
            return (result, File.Exists(outFile) ? File.ReadAllText(outFile) : null, probe);
        }
        finally
        {
            try { child.Kill(); child.WaitForExit(5000); } catch (InvalidOperationException) { }
            child.Dispose();
        }
    }

    [Fact]
    public async Task TypesExactlyRemoteControlIntoAnEmptyBox()
    {
        var (result, received, _) = await Run("");
        Assert.Equal("sent", result);
        Assert.Equal(Command, received);
    }

    [Fact]
    public async Task TypesNothingWhenTheBoxHasText()
    {
        var (result, received, probe) = await Run("half typed");
        Assert.Equal("refused not-empty", result);
        Assert.Null(received);
        Assert.Contains("9 chars after marker", probe); // "half typed" alone: nothing was typed after it
    }
}
