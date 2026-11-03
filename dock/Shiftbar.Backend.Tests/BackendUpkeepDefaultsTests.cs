using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

public sealed class BackendUpkeepDefaultsTests
{
    [Fact]
    public async Task DefaultsNeverInvokeEngineOrTouchSessionsAndSavedThresholdIsRespected()
    {
        using var temp = new TempDir();
        var before = Environment.GetEnvironmentVariable("SHIFTBAR_DATA");
        var models = ResumeStopped.Models;
        var context = SynchronizationContext.Current;
        Environment.SetEnvironmentVariable("SHIFTBAR_DATA", temp.Path);
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var engine = new Engine(Path.Combine(temp.Path, "must-never-run"));
            var settings = new DockSettings { AutoLine = false };
            Snapshot current = new(1, 1, [Account(10)]);
            var notices = new List<string>();
            var refreshes = 0;
            using var upkeep = new BackendUpkeep(engine, settings, () => current, (_, text) => notices.Add(text), () => { refreshes++; return Task.CompletedTask; });
            upkeep.ApplyEngineConfig(new Dictionary<string, string?> { ["autoswitch.threshold"] = "83.5" });
            Assert.Equal(83.5, upkeep.EffectiveThreshold);
            await upkeep.TickAsync();
            current = current with { Accounts = [Account(100)] };
            await upkeep.TickAsync();
            Assert.Empty(notices);
            Assert.Equal(0, refreshes);
            Assert.False(File.Exists(Path.Combine(temp.Path, "prime.json")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "resumed.json")));
            Assert.False(File.Exists(Path.Combine(temp.Path, "shiftbar.log")));
        }
        finally
        {
            ResumeStopped.Models = models;
            Environment.SetEnvironmentVariable("SHIFTBAR_DATA", before);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    [Fact]
    public async Task QuotaOptInReusesCrossingPolicyAndStaleReadingsNeverResetIt()
    {
        using var temp = new TempDir();
        var before = Environment.GetEnvironmentVariable("SHIFTBAR_DATA");
        var context = SynchronizationContext.Current;
        Environment.SetEnvironmentVariable("SHIFTBAR_DATA", temp.Path);
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var engine = new Engine("must-never-run");
            var settings = new DockSettings { QuotaNotifications = true };
            Snapshot current = new(1, 1, [Account(10)]);
            var notices = new List<string>();
            using var upkeep = new BackendUpkeep(engine, settings, () => current, (_, text) => notices.Add(text), () => Task.CompletedTask);
            await upkeep.TickAsync();
            current = current with { Accounts = [Account(100)] };
            await upkeep.TickAsync();
            Assert.Single(notices);
            Assert.Contains("limit reached", notices[0]);
            current = current with { Accounts = [Account(0) with { UsageStatus = "unavailable" }] };
            await upkeep.TickAsync();
            Assert.Single(notices);
            current = current with { Accounts = [Account(0)] };
            await upkeep.TickAsync();
            Assert.Equal(2, notices.Count);
            Assert.Contains("reset", notices[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHIFTBAR_DATA", before);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static Account Account(double percent) => new(1, "synthetic@example.com", true, "ok", new Usage(new Window(percent, null), null, null, null));
}
