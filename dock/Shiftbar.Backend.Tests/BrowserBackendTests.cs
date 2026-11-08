using System.Text.Json;
using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

public sealed class BrowserBackendTests
{
    static JsonElement Values(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void BrowserSettingsApplyAndOverridesStayCaseInsensitive()
    {
        var settings = new DockSettings();
        BackendHost.Apply(settings, Values(new { browserFollowsAccount = true, closeOldBrowserWindows = false, browser = "Chrome-Canary" }));
        Assert.True(settings.BrowserFollowsAccount);
        Assert.False(settings.CloseOldBrowserWindows);
        Assert.Equal("chrome-canary", settings.Browser);
        var before = settings.BrowserProfiles;
        BackendHost.Apply(settings, Values(new { browserProfile = new { email = "Synthetic@Example.com", dir = "Profile 2" } }));
        Assert.Equal("Profile 2", settings.BrowserProfiles["synthetic@example.com"]);
        Assert.Empty(before); // replaced, never edited in place: the browser step may be reading it
        BackendHost.Apply(settings, Values(new { browserProfile = new { email = "synthetic@example.com", dir = (string?)null } }));
        Assert.Empty(settings.BrowserProfiles);
        Assert.Throws<EngineException>(() => BackendHost.Apply(settings, Values(new { browser = "netscape" })));
        Assert.Equal("chrome-canary", settings.Browser);
    }

    [Fact]
    public void BrowserInfoResolvesEachAccount()
    {
        var settings = new DockSettings { BrowserProfiles = new() { ["b@example.com"] = "Profile 9" } };
        IReadOnlyList<BrowserProfile> profiles = [new("Default", "a@example.com", ""), new("Profile 9", "Work", "")];
        Snapshot snap = new(1, 1, [Account(1, "A@example.com"), Account(2, "b@example.com"), Account(3, "c@example.com")]);
        var info = JsonSerializer.SerializeToElement(BackendHost.BrowserInfo(settings, snap, _ => (profiles, new HashSet<string>())), Json.Options);
        Assert.Equal("chrome", info.GetProperty("browser").GetString());
        Assert.Equal(2, info.GetProperty("profiles").GetArrayLength());
        Assert.Equal("Work", info.GetProperty("profiles")[1].GetProperty("name").GetString());
        var resolved = info.GetProperty("resolved");
        Assert.Equal("Default", resolved.GetProperty("A@example.com").GetString()); // keys keep the account's own spelling
        Assert.Equal("Profile 9", resolved.GetProperty("b@example.com").GetString());
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("c@example.com").ValueKind);
    }

    sealed class Runner
    {
        public readonly List<(string Exe, IReadOnlyList<string> Args)> Calls = [];
        public (int, string, string) Result = (0, "", "");
        public (int, string, string) Run(string exe, IReadOnlyList<string> args) { Calls.Add((exe, args)); return Result; }
    }

    static readonly BrowserKind Canary = BrowserProfiles.Find("chrome-canary")!;

    [Fact]
    public void MacHostLaunchesAndChecksTheExactAppWithArgvOnly()
    {
        var runner = new Runner();
        var host = new MacBrowserHost(Canary, "/Applications/Google Chrome Canary.app", _ => { }, runner.Run);
        host.Launch("Profile 2");
        Assert.Equal("/usr/bin/open", runner.Calls[0].Exe);
        Assert.Equal(["-na", "/Applications/Google Chrome Canary.app", "--args", "--profile-directory=Profile 2"], runner.Calls[0].Args.ToArray());
        Assert.True(host.Running());
        Assert.Equal("/usr/bin/pgrep", runner.Calls[1].Exe);
        Assert.Equal(["-f", @"^/Applications/Google Chrome Canary\.app/Contents/MacOS/Google Chrome Canary"], runner.Calls[1].Args.ToArray());
        runner.Result = (1, "", "");
        Assert.False(host.Running());
        Assert.Throws<InvalidOperationException>(() => host.Launch("Default")); // BrowserFollow logs it
    }

    [Fact]
    public void MacHostPassesTheProfileNameAsAnArgumentAndHintsAccessibilityOnce()
    {
        var runner = new Runner { Result = (0, "2\n", "") };
        var logs = new List<string>();
        var host = new MacBrowserHost(Canary, "/Applications/Google Chrome Canary.app", logs.Add, runner.Run);
        const string name = "a\" & do shell script \"x";
        Assert.Equal(2, host.CloseWindows(name));
        var (exe, args) = runner.Calls[0];
        Assert.Equal("/usr/bin/osascript", exe);
        Assert.Equal([name, "Google Chrome Canary"], args.TakeLast(2).ToArray());
        Assert.DoesNotContain(args.SkipLast(2), a => a.Contains(name));
        Assert.Contains("repeat with t in (get toolbars of w)", args); // never the page: it may name the old profile
        Assert.DoesNotContain(args, a => a.Contains("entire contents of w"));
        runner.Result = (1, "", "execution error: System Events got an error: osascript is not allowed assistive access. (-1719)");
        Assert.Equal(0, host.CloseWindows("Work"));
        Assert.Equal(0, host.CloseWindows("Work"));
        Assert.Contains("Accessibility", Assert.Single(logs));
    }

    [Fact]
    public void TheDefaultLoginMapsToAnEmailOrToUnknown()
    {
        Snapshot snap = new(1, 1, [Account(1, "a@example.com"), Account(2, "b@example.com")]);
        Assert.Equal("b@example.com", BackendUpkeep.LoginEmail((2, DateTime.UtcNow), snap));
        Assert.Null(BackendUpkeep.LoginEmail((null, DateTime.MaxValue), snap)); // sequence.json unreadable
        Assert.Null(BackendUpkeep.LoginEmail((7, DateTime.UtcNow), snap)); // not listed yet
    }

    static Account Account(int n, string email) => new(n, email, n == 1, "ok", null);
}
