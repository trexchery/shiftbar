using System.Diagnostics;
using System.Text.Json;

namespace Shiftbar;

/// A Chromium browser Shiftbar knows. `WinExe` sits under %LOCALAPPDATA%, %ProgramFiles% or %ProgramFiles(x86)%,
/// `WinUserData` under %LOCALAPPDATA%; on macOS `Name` is the app bundle's name and `MacUserData` sits under Application Support.
public sealed record BrowserKind(string Id, string Name, string WinExe, string WinUserData, string MacUserData)
{
    public string? UserDataDir =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WinUserData)
        : OperatingSystem.IsMacOS() ? Path.Combine(StoreSecurity.UserProfile, "Library", "Application Support", MacUserData)
        : null;

    /// The installed chrome.exe/msedge.exe, or the .app bundle on macOS; null when not installed. Chrome and Canary share
    /// the exe name, so callers compare this full path, never the process name.
    public string? ExePath()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" }
                .Select(Environment.GetEnvironmentVariable).Where(r => r is { Length: > 0 })
                .Select(r => Path.Combine(r!, WinExe)).FirstOrDefault(File.Exists);
        if (OperatingSystem.IsMacOS())
            return new[] { "/Applications", Path.Combine(StoreSecurity.UserProfile, "Applications") }
                .Select(r => Path.Combine(r, Name + ".app")).FirstOrDefault(Directory.Exists);
        return null;
    }
}

/// One entry of Local State's profile.info_cache: the folder under User Data, the name shown on the avatar button, and the signed-in email.
public sealed record BrowserProfile(string Dir, string Name, string UserName);

/// The browser half of the account switch: Claude in Chrome only connects when the extension is signed in to the same
/// claude.ai account as Claude Code, so users keep one browser profile per Claude account.
public static class BrowserProfiles
{
    public static readonly IReadOnlyList<BrowserKind> Browsers =
    [
        new("chrome", "Google Chrome", @"Google\Chrome\Application\chrome.exe", @"Google\Chrome\User Data", "Google/Chrome"),
        new("chrome-canary", "Google Chrome Canary", @"Google\Chrome SxS\Application\chrome.exe", @"Google\Chrome SxS\User Data", "Google/Chrome Canary"),
        new("edge", "Microsoft Edge", @"Microsoft\Edge\Application\msedge.exe", @"Microsoft\Edge\User Data", "Microsoft Edge"),
        new("brave", "Brave Browser", @"BraveSoftware\Brave-Browser\Application\brave.exe", @"BraveSoftware\Brave-Browser\User Data", "BraveSoftware/Brave-Browser"),
    ];

    public static BrowserKind? Find(string? id) => Browsers.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<BrowserKind> Installed() => Browsers.Where(b => b.ExePath() is not null).ToList();

    /// Missing, locked or garbled Local State: null (the browser rewrites it whole, so a half-written file is rare but possible).
    static JsonElement? Profile(string userDataDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(userDataDir, "Local State")));
            return doc.RootElement.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static string Str(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static IReadOnlyList<BrowserProfile> ListProfiles(string userDataDir) =>
        Profile(userDataDir) is { } p && p.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object
            ? cache.EnumerateObject().Select(e => new BrowserProfile(e.Name, Str(e.Value, "name"), Str(e.Value, "user_name"))).ToList()
            : [];

    /// Profiles with open windows; the browser updates it while running.
    public static IReadOnlySet<string> LastActive(string userDataDir) =>
        (Profile(userDataDir) is { } p && p.TryGetProperty("last_active_profiles", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : []).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// The user's pick for `email` when that profile still exists, else the one profile named after the email or signed in with it.
    /// Two candidates (or none): null, never a guess.
    public static BrowserProfile? Resolve(string email, IReadOnlyDictionary<string, string>? overrides, IReadOnlyList<BrowserProfile> profiles)
    {
        var pick = overrides?.FirstOrDefault(o => string.Equals(o.Key, email, StringComparison.OrdinalIgnoreCase)).Value;
        if (pick is not null && profiles.FirstOrDefault(p => string.Equals(p.Dir, pick, StringComparison.OrdinalIgnoreCase)) is { } chosen) return chosen;
        var match = profiles.Where(p => string.Equals(p.Name, email, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(p.UserName, email, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return match.Count == 1 ? match[0] : null;
    }

    public static (IReadOnlyList<BrowserProfile> Profiles, IReadOnlySet<string> LastActive) ReadState(string browserId) =>
        Find(browserId)?.UserDataDir is { } dir ? (ListProfiles(dir), LastActive(dir)) : ([], new HashSet<string>());
}

/// The platform side: Windows (UI Automation), macOS (System Events), and the tests' fake.
public interface IBrowserHost
{
    bool Running();
    void Launch(string profileDir);
    /// Closes every window showing `profileName` on its avatar button; returns how many.
    int CloseWindows(string profileName);
}

public sealed record BrowserHolder(int Pid, DateTime StartedUtc);

/// The old account's profile, closed once every default-login session that started before the switch has ended:
/// a running session keeps its browser connection to the profile it started with, and loses the browser when those windows close.
public sealed record PendingClose(string Email, string Browser, string ProfileDir, string ProfileName, DateTime SwitchedAtUtc, IReadOnlyList<BrowserHolder> Holders);

/// Opens the new account's profile when the default login changes, and later closes the old one's windows.
/// `defaultSessions`: the live sessions on the default login (ConfigDir == ResumeStopped.DefaultDir).
/// Observe and Tick never throw; both lock, so the tray may call Tick off the UI thread.
public sealed class BrowserFollow
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    readonly Func<DockSettings> _settings;
    readonly Func<string, IBrowserHost?> _hostFor;
    readonly Func<IReadOnlyList<LiveSession>> _defaultSessions;
    readonly Func<DateTime> _utcNow;
    readonly string _pendingPath;
    readonly Action<string> _log;
    readonly Func<string, (IReadOnlyList<BrowserProfile> Profiles, IReadOnlySet<string> LastActive)> _readState;
    readonly Func<BrowserHolder, bool> _holderAlive;
    readonly object _gate = new();
    readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PendingClose> _pending;
    string? _last;

    public BrowserFollow(Func<DockSettings> settings, Func<string, IBrowserHost?> hostFor, Func<IReadOnlyList<LiveSession>> defaultSessions,
        Func<DateTime> utcNow, string pendingPath, Action<string> log,
        Func<string, (IReadOnlyList<BrowserProfile> Profiles, IReadOnlySet<string> LastActive)>? readState = null, Func<BrowserHolder, bool>? holderAlive = null)
    {
        (_settings, _hostFor, _defaultSessions, _utcNow, _pendingPath, _log) = (settings, hostFor, defaultSessions, utcNow, pendingPath, log);
        _readState = readState ?? BrowserProfiles.ReadState;
        _holderAlive = holderAlive ?? ProcessAlive;
        try { _pending = File.Exists(pendingPath) ? JsonSerializer.Deserialize<List<PendingClose>>(File.ReadAllText(pendingPath)) ?? [] : []; }
        catch (Exception e) { log($"browser: pending closes unreadable, starting empty ({e.Message})"); _pending = []; }
    }

    public IReadOnlyList<PendingClose> Pending { get { lock (_gate) return _pending.ToArray(); } }

    /// The default login's email now; null (unknown) is ignored. The first email seen is the baseline, not a switch.
    /// `switchedAtUtc`: when the login really changed, if known (a switch made outside Shiftbar is seen up to a listing later,
    /// and a session started in between is already on the new profile, so it must not hold the old one open).
    public void Observe(string? activeEmail, DateTime? switchedAtUtc = null)
    {
        if (activeEmail is null) return;
        lock (_gate)
        {
            // The baseline moves on even when the switch fails: a failure that repeats would otherwise be retried on every listing.
            var from = _last;
            _last = activeEmail;
            if (from is null || string.Equals(from, activeEmail, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if (_pending.RemoveAll(p => string.Equals(p.Email, activeEmail, StringComparison.OrdinalIgnoreCase)) > 0) Save(); // switched back
                Switched(from, activeEmail, switchedAtUtc);
            }
            catch (Exception e) { _log($"browser: switch to {activeEmail} skipped ({e.Message})"); }
        }
    }

    void Switched(string from, string to, DateTime? switchedAtUtc)
    {
        var s = _settings();
        if (!s.BrowserFollowsAccount) return;
        if (_hostFor(s.Browser) is not { } host) { Once("host:" + s.Browser, $"browser: {s.Browser} is not available here"); return; }
        var (profiles, active) = _readState(s.Browser);
        var target = BrowserProfiles.Resolve(to, s.BrowserProfiles, profiles);
        if (target is null) Once(to, $"browser: no {s.Browser} profile matches {to}; name one after the email or pick one in Shiftbar");
        else
            try
            {
                // Already open: leave it alone, never steal focus.
                if (!host.Running() || !active.Contains(target.Dir)) { host.Launch(target.Dir); _log($"browser: opened profile {target.Dir} for {to}"); }
            }
            catch (Exception e) { _log($"browser: launch failed ({e.Message})"); }

        if (!s.CloseOldBrowserWindows || BrowserProfiles.Resolve(from, s.BrowserProfiles, profiles) is not { } old
            || string.Equals(old.Dir, target?.Dir, StringComparison.OrdinalIgnoreCase)) return;
        var now = _utcNow() is var n && switchedAtUtc < n ? switchedAtUtc.Value : n;
        var holders = _defaultSessions().Where(x => x.StartedUtc < now).Select(x => new BrowserHolder(x.Pid, x.StartedUtc)).ToList();
        _pending.RemoveAll(p => string.Equals(p.Email, from, StringComparison.OrdinalIgnoreCase));
        _pending.Add(new PendingClose(from, s.Browser, old.Dir, old.Name, now, holders));
        Save();
    }

    /// Closes each pending profile whose holders have all ended. The caller rate-limits it (~10 s).
    /// Unknown current account (null): nothing closes, as it may be the one a pending profile belongs to.
    public void Tick(string? activeEmail)
    {
        if (activeEmail is null) return;
        lock (_gate)
        {
            try
            {
                if (_pending.Count == 0) return;
                var s = _settings();
                var now = _utcNow();
                if (!s.BrowserFollowsAccount || !s.CloseOldBrowserWindows) { _pending.Clear(); Save(); return; } // off means never close
                var changed = _pending.RemoveAll(p => now - p.SwitchedAtUtc > MaxAge) > 0;
                foreach (var p in _pending.Where(p => !p.Holders.Any(_holderAlive)).ToList())
                {
                    _pending.Remove(p);
                    changed = true;
                    try { Close(p, activeEmail, s); }
                    catch (Exception e) { _log($"browser: closing {p.ProfileName} failed ({e.Message})"); }
                }
                if (changed) Save();
            }
            catch (Exception e) { _log($"browser: {e.Message}"); }
        }
    }

    /// Asked of the process, not the session records: Claude Code rewrites those on every status change, and a tick that lands
    /// on a rewrite must not read as the session's end. A reused pid starts at another time: ended. Unknown (access denied): alive.
    internal static bool ProcessAlive(BrowserHolder h)
    {
        try { using var p = Process.GetProcessById(h.Pid); return !p.HasExited && Math.Abs((p.StartTime.ToUniversalTime() - h.StartedUtc).TotalSeconds) <= 2; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    void Close(PendingClose p, string activeEmail, DockSettings s)
    {
        var (profiles, _) = _readState(p.Browser);
        if (BrowserProfiles.Resolve(activeEmail, s.BrowserProfiles, profiles) is { } current
            && string.Equals(current.Dir, p.ProfileDir, StringComparison.OrdinalIgnoreCase)) return; // switched back to that profile
        // Windows are found by the avatar button's name: a name two profiles share could close the wrong one.
        if (profiles.FirstOrDefault(x => string.Equals(x.Dir, p.ProfileDir, StringComparison.OrdinalIgnoreCase)) is not { } profile
            || profiles.Count(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            _log($"browser: not closing {p.ProfileDir}: its profile name is gone or shared");
            return;
        }
        if (_hostFor(p.Browser) is not { } host || !host.Running()) return;
        _log($"browser: closed {host.CloseWindows(profile.Name)} window(s) of {profile.Name}");
    }

    void Once(string key, string text) { if (_logged.Add(key)) _log(text); }

    void Save()
    {
        try { AtomicFile.WriteAllText(_pendingPath, JsonSerializer.Serialize(_pending, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"browser: saving pending closes failed ({e.Message})"); }
    }
}
