using Microsoft.Win32;
using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

// The Patch Goblins -> Shiftbar carry-over, on synthetic folders and a throwaway registry key (never the real Run key).
public class MigrationTests
{
    [Fact]
    public void PatchGoblinsIsTriedFirstWithItsOwnMutexName()
    {
        Assert.Equal(("patch-goblins", "PatchGoblins", "Patch Goblins"), Program.OldNames[0]);
        Assert.Equal(new[] { "patch-goblins", "handoff", "autodock", "clautoswap" }, Program.OldNames.Select(o => o.Name));
    }

    [Fact]
    public void DataFolderMovesWholeWhenShiftbarHasNone()
    {
        using var t = new TempDir();
        var old = Path.Combine(t.Path, "patch-goblins");
        Directory.CreateDirectory(Path.Combine(old, "auto-prime"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{\"AutoPrime\":true}");
        var dest = Path.Combine(t.Path, "shiftbar");
        Program.MoveData(old, dest);
        Assert.False(Directory.Exists(old));
        Assert.Equal("{\"AutoPrime\":true}", File.ReadAllText(Path.Combine(dest, "settings.json")));
        Assert.True(Directory.Exists(Path.Combine(dest, "auto-prime")));
    }

    [Fact]
    public void DataFolderMergesIntoAnExistingOneWithoutOverwriting()
    {
        using var t = new TempDir();
        var old = Path.Combine(t.Path, "patch-goblins");
        var dest = Path.Combine(t.Path, "shiftbar");
        Directory.CreateDirectory(Path.Combine(old, "auto-prime"));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(old, "settings.json"), "old");
        File.WriteAllText(Path.Combine(dest, "settings.json"), "new");
        Program.MoveData(old, dest);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "settings.json"))); // Shiftbar's own wins
        Assert.True(Directory.Exists(Path.Combine(dest, "auto-prime")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(old, "settings.json"))); // the clash stays behind, untouched
        Program.MoveData(Path.Combine(t.Path, "missing"), dest); // nothing to move: no throw
    }

    [Fact]
    public void RetiredAgentsStayBehindAndAloneAreNothingToMigrate()
    {
        using var t = new TempDir();
        var old = Path.Combine(t.Path, "patch-goblins");
        Directory.CreateDirectory(Path.Combine(old, "agents", "worktrees"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{}");
        var dest = Path.Combine(t.Path, "shiftbar");
        Assert.True(Program.HasData(old));
        Program.MoveData(old, dest);
        Assert.True(File.Exists(Path.Combine(dest, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(dest, "agents"))); // git knows those worktrees by their old path
        Assert.True(Directory.Exists(Path.Combine(old, "agents", "worktrees")));
        Assert.False(Program.HasData(old)); // the next start has nothing to carry over
    }

    [Fact]
    public void RunValueIsSwappedOnlyWhenTheOldOneWasSet()
    {
        var path = @"Software\Shiftbar.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(path);
            Program.SwapRunValue(run, "patch-goblins", "shiftbar", "\"C:\\new\\shiftbar.exe\" --minimized");
            Assert.Null(run.GetValue("shiftbar")); // autostart was off: it stays off

            run.SetValue("patch-goblins", "\"C:\\old\\patch-goblins.exe\" --minimized");
            Program.SwapRunValue(run, "patch-goblins", "shiftbar", "\"C:\\new\\shiftbar.exe\" --minimized");
            Assert.Null(run.GetValue("patch-goblins"));
            Assert.Equal("\"C:\\new\\shiftbar.exe\" --minimized", run.GetValue("shiftbar"));

            run.SetValue("handoff", "\"C:\\older\\handoff.exe\"");
            Program.SwapRunValue(run, "handoff", "shiftbar", "\"C:\\other.exe\"");
            Assert.Null(run.GetValue("handoff"));
            Assert.Equal("\"C:\\new\\shiftbar.exe\" --minimized", run.GetValue("shiftbar")); // an existing entry is kept
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Shiftbar.Tests", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void EnvironmentFallsBackToThePreRenamePrefixes()
    {
        var name = "MIGRATION_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        try
        {
            Environment.SetEnvironmentVariable("HANDOFF_" + name, "handoff");
            Assert.Equal("handoff", DockSettings.Env(name));
            Environment.SetEnvironmentVariable("PATCH_GOBLINS_" + name, "goblins");
            Assert.Equal("goblins", DockSettings.Env(name));
            Environment.SetEnvironmentVariable("SHIFTBAR_" + name, "shiftbar");
            Assert.Equal("shiftbar", DockSettings.Env(name));
        }
        finally
        {
            foreach (var p in DockSettings.EnvPrefixes) Environment.SetEnvironmentVariable(p + name, null);
        }
    }
}

[Collection("process environment")] // sets SHIFTBAR_DATA
public class LockedMigrationTests
{
    [Fact]
    public void ALockedEntryStaysAndTheRestMoveEvenWithoutAShiftbarFolder()
    {
        using var t = new TempDir();
        var was = Environment.GetEnvironmentVariable("SHIFTBAR_DATA");
        Environment.SetEnvironmentVariable("SHIFTBAR_DATA", Path.Combine(t.Path, "log")); // the locked entry is logged: never into the real log
        try
        {
            var old = Path.Combine(t.Path, "patch-goblins");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "settings.json"), "{\"AutoPrime\":true}");
            var locked = Path.Combine(old, "prime.json");
            var dest = Path.Combine(t.Path, "shiftbar");
            using (new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Program.MoveData(old, dest);
            Assert.Equal("{\"AutoPrime\":true}", File.ReadAllText(Path.Combine(dest, "settings.json"))); // not started on defaults
            Assert.True(File.Exists(locked));
            Program.MoveData(old, dest); // the next start takes the rest
            Assert.True(File.Exists(Path.Combine(dest, "prime.json")));
            Assert.False(Directory.Exists(old));
        }
        finally { Environment.SetEnvironmentVariable("SHIFTBAR_DATA", was); }
    }
}
