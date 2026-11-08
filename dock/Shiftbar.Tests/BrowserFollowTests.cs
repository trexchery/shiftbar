using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

// All accounts and profiles here are synthetic; no real browser is touched.
public class BrowserProfilesTests
{
    static readonly BrowserProfile[] Profiles =
    [
        new("Default", "Personal", "a@example.com"),
        new("Profile 1", "B@Example.com", ""),
        new("Profile 2", "Work", "c@example.com"),
        new("Profile 3", "Work 2", "c@example.com"),
    ];

    [Fact]
    public void LocalStateListsProfilesAndOpenOnesAndToleratesMissingOrGarbledFiles()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "Local State"), """
            { "profile": { "info_cache": { "Default": { "name": "Personal", "user_name": "a@example.com" }, "Profile 1": { "name": "b@example.com" } },
                           "last_active_profiles": [ "Profile 1" ] } }
            """);
        Assert.Equal([new("Default", "Personal", "a@example.com"), new("Profile 1", "b@example.com", "")], BrowserProfiles.ListProfiles(t.Path));
        Assert.True(BrowserProfiles.LastActive(t.Path).SetEquals(["Profile 1"]));

        using var empty = new TempDir();
        Assert.Empty(BrowserProfiles.ListProfiles(empty.Path));
        Assert.Empty(BrowserProfiles.LastActive(empty.Path));
        File.WriteAllText(Path.Combine(empty.Path, "Local State"), "{ \"profile\": { \"info_ca");
        Assert.Empty(BrowserProfiles.ListProfiles(empty.Path));
        File.WriteAllText(Path.Combine(empty.Path, "Local State"), """{ "profile": { "info_cache": [], "last_active_profiles": {} } }""");
        Assert.Empty(BrowserProfiles.ListProfiles(empty.Path));
        Assert.Empty(BrowserProfiles.LastActive(empty.Path));
    }

    [Fact]
    public void ResolvesByOverrideThenByTheOneProfileNamedOrSignedInWithTheEmail()
    {
        Assert.Equal("Default", BrowserProfiles.Resolve("A@example.com", null, Profiles)?.Dir); // user_name, any case
        Assert.Equal("Profile 1", BrowserProfiles.Resolve("b@example.com", null, Profiles)?.Dir); // name
        Assert.Null(BrowserProfiles.Resolve("c@example.com", null, Profiles)); // two candidates: no guess
        Assert.Null(BrowserProfiles.Resolve("d@example.com", null, Profiles));
        var picks = new Dictionary<string, string> { ["C@EXAMPLE.com"] = "Profile 3", ["a@example.com"] = "Gone" };
        Assert.Equal("Profile 3", BrowserProfiles.Resolve("c@example.com", picks, Profiles)?.Dir);
        Assert.Equal("Default", BrowserProfiles.Resolve("a@example.com", picks, Profiles)?.Dir); // a pick that no longer exists falls back
    }

    [Fact]
    public void KnownBrowsersHaveDistinctIds() =>
        Assert.Equal(["chrome", "chrome-canary", "edge", "brave"], BrowserProfiles.Browsers.Select(b => b.Id));

    [Fact]
    public void BrowserSettingsDefaultForOlderFilesAndRoundTripCaseInsensitively()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(file, """{ "AutoSwitch": true }""");
        var s = DockSettings.Load(file);
        Assert.False(s.BrowserFollowsAccount);
        Assert.Equal("chrome", s.Browser);
        Assert.True(s.CloseOldBrowserWindows);
        Assert.Empty(s.BrowserProfiles);

        s.BrowserFollowsAccount = true;
        s.Browser = "edge";
        s.CloseOldBrowserWindows = false;
        s.BrowserProfiles["Nico@Example.com"] = "Profile 2";
        s.Save(file);
        var loaded = DockSettings.Load(file);
        Assert.True(loaded.BrowserFollowsAccount);
        Assert.Equal("edge", loaded.Browser);
        Assert.False(loaded.CloseOldBrowserWindows);
        Assert.Equal("Profile 2", loaded.BrowserProfiles["nico@example.com"]);
        File.WriteAllText(file, """{ "BrowserProfiles": null }""");
        Assert.Empty(DockSettings.Load(file).BrowserProfiles);
    }
}

public class BrowserFollowTests
{
    static readonly DateTime T0 = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    sealed class FakeHost : IBrowserHost
    {
        public bool IsRunning;
        public bool Throws;
        public List<string> Launched = [];
        public List<string> Closed = [];
        public bool Running() => Throws ? throw new InvalidOperationException("boom") : IsRunning;
        public void Launch(string profileDir) { if (Throws) throw new InvalidOperationException("boom"); Launched.Add(profileDir); IsRunning = true; }
        public int CloseWindows(string profileName) { if (Throws) throw new InvalidOperationException("boom"); Closed.Add(profileName); return 1; }
    }

    sealed class Rig : IDisposable
    {
        readonly TempDir _tmp = new();
        public string PendingPath => Path.Combine(_tmp.Path, "browser_pending.json");
        public DockSettings Settings = new() { BrowserFollowsAccount = true };
        public FakeHost Host = new();
        public List<LiveSession> Sessions = [];
        public DateTime Now = T0;
        public List<string> Log = [];
        public List<BrowserProfile> Profiles = [new("Default", "a@example.com", ""), new("Profile 1", "b@example.com", "")];
        public HashSet<string> Open = [];
        public Func<BrowserHolder, bool>? Alive; // the holder's process; by default Sessions stand in for the processes

        public BrowserFollow New() => new(() => Settings, id => id == Settings.Browser ? Host : null, () => Sessions, () => Now, PendingPath, Log.Add,
            _ => (Profiles, Open), h => Alive?.Invoke(h) ?? Sessions.Any(l => l.Pid == h.Pid && Math.Abs((l.StartedUtc - h.StartedUtc).TotalSeconds) <= 2));

        public void Session(int pid, DateTime started) =>
            Sessions.Add(new LiveSession(pid, "s" + pid, @"C:\work", ResumeStopped.DefaultDir, null, "idle", started));

        public void Dispose() => _tmp.Dispose();
    }

    [Fact]
    public void TheFirstObservationIsABaselineAndUnknownIsIgnored()
    {
        using var r = new Rig();
        var f = r.New();
        f.Observe(null);
        f.Observe("a@example.com");
        f.Observe(null);
        f.Observe("A@example.com");
        Assert.Empty(r.Host.Launched);
        Assert.Empty(f.Pending);
    }

    [Fact]
    public void ASwitchOpensTheNewProfileOnlyWhenItIsNotAlreadyOpen()
    {
        using var r = new Rig();
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com"); // browser closed
        Assert.Equal(["Profile 1"], r.Host.Launched);

        r.Open.Add("Profile 1");
        f.Observe("a@example.com"); // running, but not that profile
        Assert.Equal(["Profile 1", "Default"], r.Host.Launched);

        r.Open.Add("Default");
        f.Observe("b@example.com"); // already open: never steal focus
        Assert.Equal(2, r.Host.Launched.Count);
    }

    [Fact]
    public void DisabledDoesNothingAndAnUnmatchedAccountIsLoggedOnce()
    {
        using var r = new Rig();
        r.Settings.BrowserFollowsAccount = false;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        Assert.Empty(r.Host.Launched);
        Assert.Empty(f.Pending);

        r.Settings.BrowserFollowsAccount = true;
        f.Observe("x@example.com");
        f.Observe("a@example.com");
        f.Observe("x@example.com");
        Assert.Equal(["Default"], r.Host.Launched);
        Assert.Single(r.Log, l => l.Contains("x@example.com"));
    }

    [Fact]
    public void OldWindowsCloseOnlyAfterEverySessionFromBeforeTheSwitchEnds()
    {
        using var r = new Rig();
        r.Session(100, T0.AddHours(-1));
        r.Session(200, T0.AddMinutes(-5));
        var f = r.New();
        f.Observe("a@example.com");
        r.Session(300, T0.AddSeconds(5)); // seen as started after the switch: it picks the new profile on its own
        r.Now = T0.AddSeconds(2);
        f.Observe("b@example.com");
        var p = Assert.Single(f.Pending);
        Assert.Equal(("a@example.com", "Default", "a@example.com"), (p.Email, p.ProfileDir, p.ProfileName));
        Assert.Equal([100, 200], p.Holders.Select(h => h.Pid));

        r.Host.IsRunning = true;
        f.Tick("b@example.com");
        r.Sessions.RemoveAll(s => s.Pid == 100);
        f.Tick("b@example.com");
        Assert.Empty(r.Host.Closed);

        r.Sessions.RemoveAll(s => s.Pid == 200);
        f.Tick("b@example.com");
        Assert.Equal(["a@example.com"], r.Host.Closed);
        Assert.Empty(f.Pending);
        f.Tick("b@example.com");
        Assert.Single(r.Host.Closed);
    }

    [Fact]
    public void AHolderIsAskedOfItsProcessAndAReusedPidCountsAsEnded()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        var started = p.StartTime.ToUniversalTime();
        Assert.True(BrowserFollow.ProcessAlive(new(p.Id, started.AddSeconds(1.5)))); // clock jitter: same process
        Assert.False(BrowserFollow.ProcessAlive(new(p.Id, started.AddMinutes(-3)))); // another process got the pid
        Assert.False(BrowserFollow.ProcessAlive(new(int.MaxValue - 1, started))); // gone

        // A session record missing for a tick (Claude Code was rewriting it) while the process lives: nothing closes.
        using var r = new Rig();
        r.Session(100, T0.AddHours(-1));
        r.Host.IsRunning = true;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        r.Sessions.Clear();
        r.Alive = h => h.Pid == 100;
        f.Tick("b@example.com");
        Assert.Empty(r.Host.Closed);
        r.Alive = _ => false;
        f.Tick("b@example.com");
        Assert.Equal(["a@example.com"], r.Host.Closed);
    }

    [Fact]
    public void HoldersCountFromTheRealSwitchTimeAndAnUnknownAccountClosesNothing()
    {
        using var r = new Rig();
        r.Session(100, T0.AddMinutes(-10));
        r.Session(200, T0.AddMinutes(-1)); // started after the real switch, before Shiftbar saw it: already on the new profile
        r.Host.IsRunning = true;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com", T0.AddMinutes(-2));
        Assert.Equal([100], Assert.Single(f.Pending).Holders.Select(h => h.Pid));
        f.Observe("a@example.com", T0.AddHours(1)); // a time after now is not believed
        Assert.Equal([100, 200], Assert.Single(f.Pending).Holders.Select(h => h.Pid));

        r.Sessions.Clear();
        f.Tick(null);
        Assert.Empty(r.Host.Closed);
        Assert.Single(f.Pending);
        f.Tick("a@example.com");
        Assert.Equal(["b@example.com"], r.Host.Closed);
    }

    [Fact]
    public void SwitchingBackCancelsTheClose()
    {
        using var r = new Rig();
        r.Session(100, T0.AddHours(-1));
        r.Host.IsRunning = true;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        f.Observe("a@example.com"); // drops a's entry, records b's
        Assert.Equal(["b@example.com"], f.Pending.Select(p => p.Email));

        // Holders gone while the current account's profile is the pending one (another account mapped to it): dropped, not closed.
        r.Settings.BrowserProfiles["c@example.com"] = "Profile 1";
        r.Sessions.Clear();
        f.Tick("c@example.com");
        Assert.Empty(r.Host.Closed);
        Assert.Empty(f.Pending);
    }

    [Fact]
    public void AProfileNameTwoProfilesShareIsNeverClosed()
    {
        using var r = new Rig();
        r.Profiles.Add(new("Profile 2", "A@example.com", ""));
        r.Settings.BrowserProfiles["a@example.com"] = "Default";
        r.Host.IsRunning = true;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        Assert.Single(f.Pending);
        f.Tick("b@example.com");
        Assert.Empty(r.Host.Closed);
        Assert.Empty(f.Pending);
        Assert.Contains(r.Log, l => l.Contains("shared"));
    }

    [Fact]
    public void PendingClosesSurviveARestartAndExpireAfterAWeek()
    {
        using var r = new Rig();
        r.Session(100, T0.AddHours(-1));
        r.Host.IsRunning = true;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");

        var again = r.New();
        again.Observe("b@example.com"); // baseline after the restart
        Assert.Single(again.Pending);
        r.Sessions.Clear();
        again.Tick("b@example.com");
        Assert.Equal(["a@example.com"], r.Host.Closed);
        Assert.Empty(r.New().Pending);

        r.Session(100, T0.AddHours(-1));
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        r.Sessions.Clear();
        r.Now = T0.AddDays(8);
        r.New().Tick("b@example.com");
        Assert.Single(r.Host.Closed);
        Assert.Empty(r.New().Pending);
    }

    [Fact]
    public void TurningCloseOffDropsPendingCloses()
    {
        using var r = new Rig();
        r.Session(100, T0.AddHours(-1));
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        Assert.Single(f.Pending);
        r.Settings.CloseOldBrowserWindows = false;
        r.Sessions.Clear();
        f.Tick("b@example.com");
        Assert.Empty(f.Pending);
        f.Observe("a@example.com");
        Assert.Empty(f.Pending);
        Assert.Empty(r.Host.Closed);
    }

    [Fact]
    public void HostAndStateFailuresAreLoggedNeverThrown()
    {
        using var r = new Rig();
        r.Host.Throws = true;
        var f = r.New();
        f.Observe("a@example.com");
        f.Observe("b@example.com");
        Assert.Single(f.Pending); // a failed launch still records the close
        f.Tick("b@example.com");
        Assert.Empty(f.Pending);
        Assert.Contains(r.Log, l => l.Contains("boom"));

        File.WriteAllText(r.PendingPath, "{ garbled");
        Assert.Empty(r.New().Pending);
        var broken = new BrowserFollow(() => throw new IOException("settings"), _ => null, () => [], () => T0, r.PendingPath, r.Log.Add, _ => throw new IOException("state"));
        broken.Observe("a@example.com");
        broken.Observe("b@example.com");
        broken.Tick("b@example.com");
    }
}
