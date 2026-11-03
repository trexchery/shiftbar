using System.Text.Json;
using Microsoft.Win32;

namespace Shiftbar;

/// Dock-only preferences. Auto-switch thresholds live in the engine's
/// settings.json (`cswap config`) so the CLI and the dock never disagree.
public sealed class DockSettings
{
    public bool AutoSwitch { get; set; }
    public bool AutoSwitchPolicyAccepted { get; set; }
    public int RefreshSeconds { get; set; } = 180;
    public bool Pinned { get; set; }
    public int? PinnedX { get; set; }
    public int? PinnedY { get; set; }
    public bool PreserveRemoteControl { get; set; } = true; // keys absent from an older settings.json keep these defaults
    public bool ReconnectRemoteControl { get; set; } = true; // types /remote-control into idle default-login windows a switch dropped it in (RemoteControl)
    public bool AutoProjectAccount { get; set; }
    public bool QuotaNotifications { get; set; }
    public bool AutoPrime { get; set; }
    public bool AutoLine { get; set; } = true; // the switch line follows each account's burn (SwitchLine); off: the engine's fixed threshold
    public string? TerminalFolder { get; set; } // where "Open terminal" starts browsing; null: the user folder
    // Keys an older settings.json may still hold (Agent*, AskApproval, MoveWorkflows*) are ignored on load and dropped on the next save.

    public const int MinRefreshSeconds = 60;

    /// Variable prefixes, newest first: `SHIFTBAR_<name>`, then the pre-rename `PATCH_GOBLINS_<name>` and `HANDOFF_<name>`.
    internal static readonly string[] EnvPrefixes = { "SHIFTBAR_", "PATCH_GOBLINS_", "HANDOFF_" };

    public static string? Env(string name) =>
        EnvPrefixes.Select(p => Environment.GetEnvironmentVariable(p + name)).FirstOrDefault(v => v is { Length: > 0 });

    /// SHIFTBAR_DATA runs a separate, isolated instance (development, benchmarks).
    public static string Dir => Env("DATA") is { Length: > 0 } d ? d : Path.Combine(
        OperatingSystem.IsMacOS() ? Path.Combine(StoreSecurity.UserProfile, "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "shiftbar");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static DockSettings Load(string? path = null)
    {
        try
        {
            var s = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(path ?? FilePath)) ?? new();
            s.RefreshSeconds = Math.Max(MinRefreshSeconds, s.RefreshSeconds);
            return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string? path = null) =>
        AtomicFile.WriteAllText(path ?? FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class AtomicFile
{
    /// Write to a sibling temp file, then rename over the target, so a crash
    /// never leaves a half-written file.
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

public static class Startup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Name = "shiftbar";
    internal static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists(LoginAgent);
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) { SetLoginAgent(enabled); return; }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, Command);
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }

    static string LoginAgent => Path.Combine(StoreSecurity.UserProfile, "Library", "LaunchAgents", "com.shiftbar.app.plist");

    static void SetLoginAgent(bool enabled)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Launch at login requires macOS");
        if (!enabled) { File.Delete(LoginAgent); return; }
        var executable = DockSettings.Env("APP_EXECUTABLE") ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Application executable unavailable");
        var xml = new System.Xml.Linq.XElement("plist", new System.Xml.Linq.XAttribute("version", "1.0"),
            new System.Xml.Linq.XElement("dict", new System.Xml.Linq.XElement("key", "Label"), new System.Xml.Linq.XElement("string", "com.shiftbar.app"),
                new System.Xml.Linq.XElement("key", "ProgramArguments"), new System.Xml.Linq.XElement("array", new System.Xml.Linq.XElement("string", executable)),
                new System.Xml.Linq.XElement("key", "RunAtLoad"), new System.Xml.Linq.XElement("true")));
        AtomicFile.WriteAllText(LoginAgent, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml);
        File.SetUnixFileMode(LoginAgent, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
