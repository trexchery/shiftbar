using System.Diagnostics;
using System.Text.Json;
using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

public sealed class BackendUpkeepTests
{
    [Fact]
    public async Task IdleAndLimitedUserSessionsAreNeverTerminatedByUpkeep()
    {
        if (OperatingSystem.IsWindows()) return; // This backend is used on macOS; CI also runs it on Linux.
        using var tmp = new TempDir();
        using var loop = new Loop();
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var oldData = Environment.GetEnvironmentVariable("SHIFTBAR_DATA");
        Environment.SetEnvironmentVariable("HOME", tmp.Path);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(tmp.Path, "xdg"));
        Environment.SetEnvironmentVariable("SHIFTBAR_DATA", Path.Combine(tmp.Path, "desktop"));
        using var child = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 60"]) { UseShellExecute = false })!;
        try
        {
            var dir = Path.Combine(StoreSecurity.BackupRoot, "sessions", "2-spare");
            var records = Path.Combine(dir, "sessions");
            var project = Path.Combine(dir, "projects", "test");
            Directory.CreateDirectory(records);
            Directory.CreateDirectory(project);
            var transcript = Path.Combine(project, "sid.jsonl");
            var record = Path.Combine(records, child.Id + ".json");
            File.WriteAllText(record, JsonSerializer.Serialize(new { pid = child.Id, sessionId = "sid", cwd = tmp.Path, status = "idle", kind = "interactive" }));
            File.WriteAllText(transcript, """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"Done."}]}}""");
            File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddMinutes(-5));
            var reset = DateTimeOffset.Now.AddHours(2);
            Account A(int n, double quota, bool active) => new(n, $"synthetic{n}@example.invalid", active, "ok",
                new Usage(new Window(quota, reset), new Window(quota, reset), null, null));
            var snapshot = new Snapshot(1, 1, [A(1, 100, true), A(2, 10, false)]);
            var notices = new List<string>();
            Assert.Single(ResumeStopped.Scan([(dir, 2)], DateTime.UtcNow, includeBusy: true));
            await loop.Run(async () =>
            {
                using var upkeep = new BackendUpkeep(new Engine("/bin/true"), new DockSettings { AutoSwitch = true },
                    () => snapshot, (_, text) => notices.Add(text), () => Task.CompletedTask, followsLogins: false);
                await upkeep.ResumeAsync(snapshot);
                return true;
            });
            Assert.False(child.HasExited);
            Assert.True(File.Exists(record));
            Assert.Empty(notices); // idle, not at a limit: nothing to say
            var limited = new Snapshot(1, 1, [A(1, 10, true), A(2, 100, false)]);
            File.WriteAllText(transcript, JsonSerializer.Serialize(new
            {
                type = "assistant", timestamp = DateTimeOffset.UtcNow.AddSeconds(2), isApiErrorMessage = true,
                apiError = "usage_limit_reached", apiErrorParams = new { rate_limit_info = new { status = "rejected", rateLimitType = "five_hour" } },
                message = new { role = "assistant", content = new[] { new { type = "text", text = "You've hit your limit" } } },
            }));
            Assert.True(Assert.Single(ResumeStopped.Scan([(dir, 2)], DateTime.UtcNow, includeBusy: true)).AtLimit);
            await loop.Run(async () =>
            {
                using var upkeep = new BackendUpkeep(new Engine("/bin/true"), new DockSettings { AutoSwitch = true },
                    () => limited, (_, text) => notices.Add(text), () => Task.CompletedTask, followsLogins: false);
                await upkeep.ResumeAsync(limited);
                return true;
            });
            Assert.False(child.HasExited);
            Assert.True(File.Exists(record));
            Assert.Contains(notices, text => text.Contains("remains open", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            Environment.SetEnvironmentVariable("HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
            Environment.SetEnvironmentVariable("SHIFTBAR_DATA", oldData);
        }
    }

}
