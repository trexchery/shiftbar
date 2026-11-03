using System.Diagnostics;
using System.Text.Json;
using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

public sealed class PosixAdapterTests
{
    /// A window running a workflow (another session's too, after a /clear) is never movable, so it is never closed.
    [Fact]
    public void AWindowRunningAWorkflowIsNeverMovedOnPosix()
    {
        using var tmp = new TempDir();
        var project = Path.Combine(tmp.Path, "projects", "-w");
        var run = Path.Combine(project, "old-sid", "subagents", "workflows", "wf_a");
        Directory.CreateDirectory(run);
        File.WriteAllLines(Path.Combine(run, "journal.jsonl"), ["""{"type":"launched"}""", """{"type":"started","key":"k","agentId":"a","label":"x","phase":"P"}"""]);
        var runs = ResumeStopped.RunningWorkflows(project, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal("wf_a", Assert.Single(runs).RunId);
        var idle = new StoppedSession(1, "sid", "/w", Path.Combine(tmp.Path, "profile"), 3, Path.Combine(project, "sid.jsonl"), false, Runs: runs);
        Assert.False(idle.Movable);
        Assert.False((idle with { AtLimit = true }).Movable);
        Assert.True((idle with { Runs = null }).Movable);
    }

    /// The check right before a close holds only for the process that wrote the record: a missing, unreadable or other
    /// start stamp is not a match (unknown means alive only where nothing is killed). Reads only; this process is the pid.
    [Fact]
    public void AWindowIsClosedOnlyWhenTheRecordProvesItsProcess()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX start stamps: runs on Linux and macOS
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "sid.jsonl");
        var record = Path.Combine(tmp.Path, "1.json");
        File.WriteAllText(transcript, """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"All done."}]}}""");
        var s = new StoppedSession(Environment.ProcessId, "sid", tmp.Path, tmp.Path, 3, transcript, false, record, File.GetLastWriteTimeUtc(transcript));
        Assert.True(s.Movable);
        bool With(string? procStart)
        {
            File.WriteAllText(record, JsonSerializer.Serialize(new { pid = Environment.ProcessId, sessionId = "sid", cwd = tmp.Path, status = "idle", kind = "interactive", procStart }));
            return ResumeStopped.Unchanged(s);
        }
        string real, other;
        if (OperatingSystem.IsLinux())
        {
            var stat = File.ReadAllText($"/proc/{Environment.ProcessId}/stat"); // field 22, start time in clock ticks
            real = stat[(stat.LastIndexOf(')') + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[19];
            other = (long.Parse(real) + 1000).ToString();
        }
        else
        {
            string Lstart(DateTime t) => t.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture); // ps lstart, C locale, UTC
            var start = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            (real, other) = (Lstart(start), Lstart(start.AddHours(-1)));
        }
        Assert.False(With(null));
        Assert.False(With("not a start time"));
        Assert.False(With(other));
        Assert.True(With(real));
    }

    [Fact]
    public void HardeningRetainsExecutablesAndDoesNotFollowLinks()
    {
        if (OperatingSystem.IsWindows()) return; // Unix file modes: runs on Linux and macOS
        using var temp = new TempDir();
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(data);
        var executable = Path.Combine(data, "verify.sh");
        File.WriteAllText(executable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var outside = Path.Combine(temp.Path, "outside");
        File.WriteAllText(outside, "synthetic");
        File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        File.CreateSymbolicLink(Path.Combine(data, "link"), outside);
        StoreSecurity.Harden(data);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(executable));
        Assert.True(File.GetUnixFileMode(outside).HasFlag(UnixFileMode.OtherRead));
    }
}
