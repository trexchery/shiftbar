namespace Shiftbar;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // The Remote Control helper (ConsoleNudge): its own short-lived process, never a tray, so before the mutex.
        if (args is ["rc-nudge", ..]) { Environment.ExitCode = ConsoleNudge.Run(args[1..]); return; }
        // Only `--minimized` starts the tray. Anything else is a caller of a retired verb (an older build's `guard` hook or
        // `agents-mcp` server in a still-running claude): refuse it, exit 2 so a hook blocks the tool call, and never start a tray under it.
        if (args.Any(a => a != "--minimized"))
        {
            Console.Error.WriteLine($"shiftbar: '{args.First(a => a != "--minimized")}' is not supported by this version.");
            Environment.ExitCode = 2;
            return;
        }
        var isolated = DockSettings.Env("DATA") is { Length: > 0 } ? "-" + DirKey(DockSettings.Dir) : "";
        using var mutex = new Mutex(true, @"Local\Shiftbar-" + Environment.UserName + isolated, out var first);
        if (!first) return; // already running in the tray

        if (!MigrateFromOldNames()) return;
        // The old exe may still be installed (a pinned icon, a Start menu entry): its tray finds its mutex held and quits.
        using var legacy = isolated.Length == 0 ? new Mutex(true, @"Local\PatchGoblins-" + Environment.UserName, out _) : null;

        // A bug in a UI handler must never take the tray down silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Engine engine;
        try { engine = Engine.Locate(); }
        catch (EngineException e)
        {
            MessageBox.Show(e.Message, "Shiftbar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(engine, minimized: args.Contains("--minimized")));
    }

    /// The pre-rename installs, newest first so the live install's data wins over an old leftover: folder and Run value,
    /// the old tray's mutex name, and the name its message uses. Engine data (~/.claude-swap-backup) never moved.
    internal static readonly (string Name, string Mutex, string Title)[] OldNames =
    {
        ("patch-goblins", "PatchGoblins", "Patch Goblins"), ("handoff", "Handoff", "handoff"),
        ("autodock", "Autodock", "autodock"), ("clautoswap", "Clautoswap", "clautoswap"),
    };

    // One-time carry-over from the pre-rename names: settings folder and autostart entry.
    // False when an old tray is still running: two trays must never manage the same accounts.
    static bool MigrateFromOldNames()
    {
        if (DockSettings.Env("DATA") is { Length: > 0 }) return true; // isolated instance
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (name, mutex, title) in OldNames)
        {
            var old = Path.Combine(local, name);
            if (!HasData(old) && Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Startup.RunKey)?.GetValue(name) is not string) continue;
            if (OldTrayRunning(name, mutex, title) is { } running)
            {
                MessageBox.Show(running + " Then start Shiftbar again: it carries over your settings.", "Shiftbar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            MoveData(old, DockSettings.Dir);
            try
            {
                using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Startup.RunKey, writable: true);
                if (run != null) SwapRunValue(run, name, Startup.Name, Startup.Command);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log(e); }
        }
        return true;
    }

    /// What still runs of the old install, said for the user, or null: its tray holds its mutex; (Patch Goblins) another
    /// patch-goblins.exe of this session is an old agents terminal's `agents-mcp`, or a tray that is closing.
    static string? OldTrayRunning(string name, string mutex, string title)
    {
        if (Mutex.TryOpenExisting($@"Local\{mutex}-" + Environment.UserName, out var held))
        {
            held.Dispose();
            return $"The old {title} tray is still running. Quit it (tray icon, Exit).";
        }
        if (name != "patch-goblins") return null; // the older names are generic words: their mutex alone decides
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var running = System.Diagnostics.Process.GetProcessesByName(name);
        try
        {
            return running.Any(p => p.SessionId == session)
                ? $"A Claude Code session that an old {title} agents terminal opened still runs {name}.exe (or the old tray is still closing). End that session."
                : null;
        }
        finally { foreach (var p in running) p.Dispose(); }
    }

    /// What stays in the old folder: Patch Goblins' retired agents and their git worktrees, which git knows by that path.
    const string LeftBehind = "agents";

    /// Whether an old data folder holds anything still to carry over.
    internal static bool HasData(string old)
    {
        try { return Directory.Exists(old) && Directory.EnumerateFileSystemEntries(old).Any(e => Path.GetFileName(e) != LeftBehind); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// Moves an old data folder's entries into `dest`, one by one: one locked entry (a folder a still-running process works
    /// in) stays behind and the rest move. Entries `dest` already has (only a log from an earlier start, say) win; the
    /// clashing old ones stay behind, as does `agents`.
    internal static void MoveData(string old, string dest)
    {
        try
        {
            if (!Directory.Exists(old)) return;
            Directory.CreateDirectory(dest);
            foreach (var entry in Directory.EnumerateFileSystemEntries(old).ToList())
            {
                var target = Path.Combine(dest, Path.GetFileName(entry));
                if (Path.GetFileName(entry) == LeftBehind || File.Exists(target) || Directory.Exists(target)) continue;
                try { if (Directory.Exists(entry)) Directory.Move(entry, target); else File.Move(entry, target); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); } // one locked entry stays; the rest move
            }
            if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
    }

    /// Autostart follows the rename: the old value goes, and the new one is added only when the old one was set.
    internal static void SwapRunValue(Microsoft.Win32.RegistryKey run, string oldName, string newName, string command)
    {
        if (run.GetValue(oldName) is not string) return;
        run.DeleteValue(oldName, throwOnMissingValue: false);
        if (run.GetValue(newName) is not string) run.SetValue(newName, command);
    }

    /// A short stable key for an isolated instance's data folder (its mutex suffix): 6 hex digits of the path's SHA-256.
    internal static string DirKey(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..6].ToLowerInvariant();
    }

    static string LogPath => Path.Combine(DockSettings.Dir, "shiftbar.log");

    static void Report(Exception? e, bool fatal)
    {
        Log(e);
        MessageBox.Show($"Shiftbar hit an unexpected error{(fatal ? " and has to close" : "")}.\n\n" +
                        $"{Engine.Redact(e?.Message ?? "unknown")}\n\nDetails: {LogPath}",
            "Shiftbar", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// Local only, redacted, capped at ~1 MB.
    internal static void Log(Exception? e) => Log(e?.ToString() ?? "unknown");

    internal static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(DockSettings.Dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {Engine.Redact(text)}\n\n");
        }
        catch { }
    }
}
