namespace PatchGoblins;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Only `--minimized` starts the tray. Anything else is a caller of a retired verb (an older build's `guard` hook or
        // `agents-mcp` server in a still-running claude): refuse it, exit 2 so a hook blocks the tool call, and never start a tray under it.
        if (args.Any(a => a != "--minimized"))
        {
            Console.Error.WriteLine($"patch-goblins: '{args.First(a => a != "--minimized")}' is not supported by this version.");
            Environment.ExitCode = 2;
            return;
        }
        var isolated = DockSettings.Env("DATA") is { Length: > 0 } ? "-" + DirKey(DockSettings.Dir) : "";
        using var mutex = new Mutex(true, @"Local\PatchGoblins-" + Environment.UserName + isolated, out var first);
        if (!first) return; // already running in the tray

        if (!MigrateFromOldNames()) return;

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
            MessageBox.Show(e.Message, "Patch Goblins", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(engine, minimized: args.Contains("--minimized")));
    }

    // One-time carry-over from the pre-rename names (clautoswap, autodock, then handoff): settings folder and autostart entry.
    // False when an old tray is still running: two trays must never manage the same accounts.
    static bool MigrateFromOldNames()
    {
        if (DockSettings.Env("DATA") is { Length: > 0 }) return true; // isolated instance
        foreach (var name in new[] { "handoff", "autodock", "clautoswap" }) // newest first: the live install's data wins over an old leftover
            if (!MigrateFrom(name)) return false;
        return true;
    }

    static bool MigrateFrom(string name)
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
        var oldRun = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue(name) is string;
        if (!Directory.Exists(old) && !oldRun) return true;
        if (Mutex.TryOpenExisting($@"Local\{char.ToUpperInvariant(name[0])}{name[1..]}-" + Environment.UserName, out var oldTray))
        {
            oldTray.Dispose();
            MessageBox.Show($"The old {name} tray is still running. Quit it (tray icon, Exit), then start Patch Goblins again: it carries over your settings.",
                "Patch Goblins", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(DockSettings.Dir)) Directory.Move(old, DockSettings.Dir);
            // A Patch Goblins folder that already exists (say, only a log from an earlier start, or what moved before an entry
            // was locked) is not a reason to leave everything behind: move the old entries in beside what is there.
            else if (Directory.Exists(old))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(old))
                {
                    var dest = Path.Combine(DockSettings.Dir, Path.GetFileName(entry));
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    try { if (Directory.Exists(entry)) Directory.Move(entry, dest); else File.Move(entry, dest); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); } // one locked entry stays; the rest move
                }
                if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue(name) is string) { run.DeleteValue(name); if (!Startup.IsEnabled()) Startup.Set(true); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log(e); }
        return true;
    }

    /// A short stable key for an isolated instance's data folder (its mutex suffix): 6 hex digits of the path's SHA-256.
    internal static string DirKey(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..6].ToLowerInvariant();
    }

    static string LogPath => Path.Combine(DockSettings.Dir, "patch-goblins.log");

    static void Report(Exception? e, bool fatal)
    {
        Log(e);
        MessageBox.Show($"Patch Goblins hit an unexpected error{(fatal ? " and has to close" : "")}.\n\n" +
                        $"{Engine.Redact(e?.Message ?? "unknown")}\n\nDetails: {LogPath}",
            "Patch Goblins", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
