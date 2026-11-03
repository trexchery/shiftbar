using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Shiftbar;

/// Remote Control (RC) across default-login switches. While RC is connected Claude Code appends `bridge-session` records
/// (`ownerAccountUuid`: the login it connected as) to the session's transcript; nothing is written when it drops. A switch of
/// the default login drops RC in every running default-login window and Claude Code never reconnects it by itself, while
/// `/remote-control` typed into the window does (a new `bridge-session` record owned by the new login follows within a second).
/// So after a switch an idle window that had RC on the previous login is sent `/remote-control` by `ConsoleNudge`, behind gates
/// that fail closed. Windows only: on macOS nothing is typed (TODO: a macOS equivalent would need the Accessibility API; until
/// then the user types /remote-control, see docs/macos.md).
public static class RemoteControl
{
    /// A window counts as idle once its transcript is this quiet, and the user as away after this long without input.
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10), Away = TimeSpan.FromSeconds(30);
    const int ScanBytes = 2 * 1024 * 1024;

    /// Whether the lines hold a `bridge-session` record, and the newest one's owner. `since` (the window's process start): a
    /// newest record followed by a line stamped well before it was written by an earlier run of a resumed session, so it
    /// does not count.
    public static (bool Used, string? Owner) Bridge(IEnumerable<string> lines, DateTime? since = null)
    {
        var (used, owner, stale) = (false, (string?)null, false);
        foreach (var line in lines)
        {
            var bridge = line.Contains("bridge-session", StringComparison.Ordinal);
            if (!bridge && (since == null || !used || stale || !line.Contains("\"timestamp\"", StringComparison.Ordinal))) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (Str(doc.RootElement, "type") == "bridge-session") (used, owner, stale) = (true, Str(doc.RootElement, "ownerAccountUuid"), false);
                else if (since != null && Str(doc.RootElement, "timestamp") is { } ts
                    && DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    && at.ToUniversalTime() < since.Value.AddMinutes(-1)) stale = true;
            }
            catch (JsonException) { } // a line cut by the tail window, or one being written
        }
        return stale ? (false, null) : (used, owner);
    }

    /// `Bridge` of a transcript's last 2 MB; nothing when it cannot be read.
    public static (bool Used, string? Owner) BridgeOf(string transcript, DateTime? since = null)
    {
        try { return Bridge(ResumeStopped.Tail(transcript, ScanBytes), since); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (false, null); }
    }

    /// Whether the newest `bridge-session` record written past byte `mark` (the transcript's length when the reconnect was
    /// queued) is owned by `uuid`: an older record of that login is no proof of a reconnect.
    public static bool ConnectedSince(string transcript, long mark, string uuid)
    {
        try
        {
            using var f = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(mark, f.Length - ScanBytes);
            if (start >= f.Length) return false;
            f.Seek(start, SeekOrigin.Begin);
            var buf = new byte[f.Length - start];
            f.ReadExactly(buf);
            return Bridge(Encoding.UTF8.GetString(buf).Split('\n')).Owner == uuid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// Whether a switch from `was` to `now` (account uuids) dropped this window's RC: its newest connection was the previous
    /// login's. An RC left over from an older login is not this switch's to restore, and an unknown previous login is no proof.
    public static bool Dropped((bool Used, string? Owner) bridge, string? was, string now) =>
        bridge is { Used: true, Owner: { } owner } && owner == was && owner != now;

    /// The default login's account uuid (`oauthAccount.accountUuid` of the default profile's global config, the file the
    /// engine's `get_default_global_config_path` names). Only that one field is read; null when unreadable.
    public static string? LoginUuid(string? path = null)
    {
        try
        {
            path ??= DefaultConfigPath();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("oauthAccount", out var o)
                && o.ValueKind == JsonValueKind.Object && Str(o, "accountUuid") is { Length: > 0 } uuid ? uuid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// Claude Code's choice: a non-empty legacy `~/.claude/.config.json`, else `~/.claude.json`.
    static string DefaultConfigPath()
    {
        var legacy = Path.Combine(ResumeStopped.DefaultDir, ".config.json");
        return File.Exists(legacy) && new FileInfo(legacy).Length > 0 ? legacy : Path.Combine(StoreSecurity.UserProfile, ".claude.json");
    }

    /// Why a window must not be typed into now, or null when it may: its record says idle, its files have been quiet for
    /// `Quiet`, its last message is a finished reply, and (unless the user asked, `manual`) nobody has touched the keyboard or
    /// mouse for `Away`. `sinceInput` null: unknown, which is not away.
    public static string? Gate(DateTime nowUtc, DateTime lastWriteUtc, bool endsTurn, string? status, TimeSpan? sinceInput, bool manual)
    {
        if (status != "idle") return "busy";
        if (nowUtc - lastWriteUtc < Quiet) return "recent-activity";
        if (!endsTurn) return "mid-turn";
        if (!manual && !(sinceInput >= Away)) return "user-active";
        return null;
    }

    /// `Gate` for a live default-login window, read from disk now: its session record (same pid and session, the same
    /// process), its transcript and session folder, and the user's last input.
    public static string? Ready(string configDir, int pid, string sessionId, bool manual)
    {
        try
        {
            foreach (var rec in Directory.GetFiles(Path.Combine(configDir, "sessions"), "*.json"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rec));
                var r = doc.RootElement;
                if (!r.TryGetProperty("pid", out var p) || !p.TryGetInt32(out var id) || id != pid || Str(r, "sessionId") != sessionId) continue;
                if (!ResumeStopped.IsLiveClaude(pid, Str(r, "procStart"), kill: true)) return "not-live";
                if (Str(r, "cwd") is not { } cwd || ResumeStopped.TranscriptOf(configDir, sessionId, cwd) is not { } t) return "no-transcript";
                return Gate(DateTime.UtcNow, ResumeStopped.Activity(t, sessionId), ResumeStopped.EndsTurn(ResumeStopped.Tail(t)), Str(r, "status"), SinceInput(), manual);
            }
            return "no-session";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return "unreadable"; }
    }

    /// `claudeArgs` plus `--remote-control` when `on`, the transcript shows RC was connected, and the args do not ask already.
    /// The stopped session's own transcript decides, whatever `remoteControlAtStartup` says: the engine's `--restore-rc` looks
    /// only at the profile the new window runs in.
    public static string[] WithRc(string[] claudeArgs, string transcript, bool on) =>
        !on || claudeArgs.Any(a => a is "--remote-control" or "--rc" || a.StartsWith("--remote-control=") || a.StartsWith("--rc="))
            || !BridgeOf(transcript).Used ? claudeArgs : [.. claudeArgs, "--remote-control"];

    /// How long since the last keyboard or mouse input in this session; null when it cannot be read.
    public static TimeSpan? SinceInput()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : null;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}

/// The reconnects owed after switches, one per window: queued at a switch, typed when the gates allow, confirmed by a new
/// `bridge-session` record owned by the new login. Unconfirmed after `ConfirmWait` it is tried once more after `RetryWait`,
/// then given up (the tray offers a button). Pure: the clock and every observation are passed in.
public sealed class RcReconnect
{
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(15), ConfirmWait = TimeSpan.FromSeconds(60),
        RetryWait = TimeSpan.FromMinutes(2), Backoff = TimeSpan.FromSeconds(10), Patience = TimeSpan.FromMinutes(30);
    /// Times `/remote-control` may be typed for one switch: the first try and its one retry.
    public const int MaxTries = 2;

    public enum Step { Wait, Nudge, Done, GiveUp, Drop }

    /// `mark`: the transcript's length when first queued; only a record past it confirms.
    public sealed class Entry(string sessionId, int pid, string transcript, string cwd, string newUuid, int switchId, long mark = 0)
    {
        public long Mark { get; } = mark;
        public string SessionId { get; } = sessionId;
        public int Pid { get; } = pid;
        public string Transcript { get; } = transcript;
        public string Cwd { get; } = cwd;
        public string NewUuid { get; } = newUuid;
        public int Switch { get; } = switchId;
        public bool Sent { get; internal set; }
        public int Tries { get; internal set; }
        public DateTime NotBefore { get; internal set; }
        public DateTime SentAt { get; internal set; }
        public DateTime GiveUpAt { get; internal set; }
        public string Folder => Path.GetFileName(Cwd.TrimEnd('\\', '/'));
    }

    readonly Dictionary<string, Entry> _live = [];
    readonly HashSet<string> _queued = []; // "session#switch": never queued twice for one switch
    readonly Dictionary<string, long> _lapsed = []; // given up on, so still without RC: owed again at the next switch

    public IReadOnlyCollection<Entry> Entries => _live.Values;
    /// The windows a new switch owes a reconnect whatever their newest record says (still owed, or given up on), with their marks.
    public Dictionary<string, long> Owed()
    {
        var owed = new Dictionary<string, long>(_lapsed);
        foreach (var e in _live.Values) owed[e.SessionId] = e.Mark;
        return owed;
    }
    /// Whether `e` is still the reconnect owed to its window (a later switch replaces it).
    public bool Owes(Entry e) => _live.TryGetValue(e.SessionId, out var cur) && cur == e;

    void Forget(Entry e) { if (Owes(e)) _live.Remove(e.SessionId); }

    /// Owes `e` a reconnect, replacing one owed for an earlier switch. False when this switch already queued it.
    public bool Queue(Entry e, DateTime now)
    {
        if (!_queued.Add($"{e.SessionId}#{e.Switch}")) return false;
        e.NotBefore = now + FirstWait;
        e.GiveUpAt = now + Patience;
        _live[e.SessionId] = e;
        _lapsed.Remove(e.SessionId);
        return true;
    }

    /// A reconnect the user asked for and the helper already typed: only its confirmation is waited for, no retry.
    public void Track(Entry e, DateTime now)
    {
        (e.Sent, e.SentAt, e.Tries) = (true, now, MaxTries);
        _live[e.SessionId] = e;
    }

    /// What to do with `e` now. `alive`: its window still runs; `confirmed`: its newest RC connection is the new login's.
    /// Done, GiveUp and Drop forget it.
    public Step Next(Entry e, DateTime now, bool alive, bool confirmed)
    {
        var step = !alive ? Step.Drop
            : confirmed ? Step.Done
            : e.Sent ? (now - e.SentAt < ConfirmWait ? Step.Wait : e.Tries >= MaxTries ? Step.GiveUp : Retry(e, now))
            : e.Tries >= MaxTries || now >= e.GiveUpAt ? Step.GiveUp
            : now >= e.NotBefore ? Step.Nudge : Step.Wait;
        if (step is Step.Done or Step.GiveUp or Step.Drop) Forget(e);
        if (step == Step.GiveUp) _lapsed[e.SessionId] = e.Mark;
        return step;
    }

    static Step Retry(Entry e, DateTime now)
    {
        (e.Sent, e.NotBefore, e.GiveUpAt) = (false, now + RetryWait, now + RetryWait + Patience);
        return Step.Wait;
    }

    /// The tray's own gate said not yet: look again after `Backoff`.
    public void Later(Entry e, DateTime now) => e.NotBefore = now + Backoff;

    /// The helper's answer (`ConsoleNudge` result line). "sent": typed and entered, so its confirmation is waited for.
    /// "aborted": typed and removed again, a try spent. "left": something other than our text sits in the box, nothing
    /// more is typed (GiveUp). Anything else typed nothing: tried again after `Backoff`.
    public Step Report(Entry e, string result, DateTime now)
    {
        switch (result.Split(' ', 2)[0])
        {
            case "sent":
                (e.Sent, e.SentAt) = (true, now);
                e.Tries++;
                return Step.Wait;
            case "aborted":
                e.Tries++;
                e.NotBefore = now + Backoff;
                return Step.Wait;
            case "left":
                Forget(e); // not lapsed: something sits in that box, nothing is typed there again
                return Step.GiveUp;
            default:
                e.NotBefore = now + Backoff;
                return Step.Wait;
        }
    }
}

/// The tray's side: notice switches of the default login, queue the windows that lost RC, drive the queue from the watch poll.
public sealed partial class TrayApp
{
    readonly RcReconnect _rc = new();
    int? _rcActive; // the default login as last observed, and its account uuid read at the same time
    string? _rcUuid;
    int _rcSwitch;
    bool _rcBusy;
    DateTime _rcNext; // the queue is looked at every few seconds, not on every watch poll

    /// Every listing and every switch the tray makes: a change of the default login (manual, automatic, at a limit, or from
    /// outside) queues reconnects for the windows it dropped.
    void RcObserve(int? active)
    {
        if (active == null || !OperatingSystem.IsWindows() || active == _rcActive && _rcUuid != null) return;
        var uuid = RemoteControl.LoginUuid(); // read with the account number it goes with, at each change
        if (uuid == null && _rcActive != null && _rcActive != active) return; // unreadable now (being rewritten): the change is seen again next time
        if (_rcActive is { } was && was != active && uuid != null && _settings.ReconnectRemoteControl)
            _ = RcQueueAsync(_rcUuid, uuid, ++_rcSwitch);
        (_rcActive, _rcUuid) = (active, uuid);
    }

    async Task RcQueueAsync(string? was, string now, int switchId)
    {
        try
        {
            var owed = _rc.Owed(); // still owed from an earlier switch: owed on this one's login now, past the same mark
            var found = await Task.Run(() =>
            {
                var list = new List<RcReconnect.Entry>();
                foreach (var s in ResumeStopped.LiveSessions([(ResumeStopped.DefaultDir, null)]))
                {
                    if (ResumeStopped.TranscriptOf(s.ConfigDir, s.SessionId, s.Cwd) is not { } t) continue;
                    if (!owed.TryGetValue(s.SessionId, out var mark))
                    {
                        try { mark = new FileInfo(t).Length; } // taken before the read: a record appended meanwhile is past it
                        catch (IOException) { continue; }
                        if (!RemoteControl.Dropped(RemoteControl.BridgeOf(t, s.StartedUtc), was, now)) continue;
                    }
                    list.Add(new(s.SessionId, s.Pid, t, s.Cwd, now, switchId, mark));
                }
                return list;
            });
            var queued = found.Count(e => _rc.Queue(e, DateTime.UtcNow));
            if (queued > 0) Program.Log($"remote control: {queued} window(s) to reconnect after switch {switchId}");
        }
        catch (Exception e) { Program.Log(e); }
    }

    /// From the watch poll: each owed window is confirmed, waited on, typed into or given up on.
    async Task RcTickAsync()
    {
        if (_rcBusy || _rc.Entries.Count == 0 || DateTime.UtcNow < _rcNext) return;
        (_rcBusy, _rcNext) = (true, DateTime.UtcNow.AddSeconds(5));
        try
        {
            foreach (var e in _rc.Entries.ToList())
            {
                if (!_rc.Owes(e)) continue; // replaced by a later switch while an earlier one was being typed
                var (alive, confirmed) = await Task.Run(() => (ResumeStopped.LiveSessions([(ResumeStopped.DefaultDir, null)]).Any(s => s.Pid == e.Pid && s.SessionId == e.SessionId),
                    RemoteControl.ConnectedSince(e.Transcript, e.Mark, e.NewUuid)));
                switch (_rc.Next(e, DateTime.UtcNow, alive, confirmed))
                {
                    case RcReconnect.Step.Done:
                        Program.Log($"remote control: {e.SessionId} reconnected");
                        break;
                    case RcReconnect.Step.GiveUp:
                        RcDropped(e);
                        break;
                    case RcReconnect.Step.Nudge when !_settings.ReconnectRemoteControl:
                        _rc.Later(e, DateTime.UtcNow);
                        break;
                    case RcReconnect.Step.Nudge:
                        if (await Task.Run(() => RemoteControl.Ready(ResumeStopped.DefaultDir, e.Pid, e.SessionId, manual: false)) != null) { _rc.Later(e, DateTime.UtcNow); break; }
                        var result = await ConsoleNudge.RunAsync(Environment.ProcessPath!, e.Pid, e.SessionId, manual: false);
                        Program.Log($"remote control: {e.SessionId} {result}");
                        if (_rc.Report(e, result, DateTime.UtcNow) == RcReconnect.Step.GiveUp) RcDropped(e, left: true);
                        break;
                }
            }
        }
        catch (Exception e) { Program.Log(e); }
        finally { _rcBusy = false; }
    }

    void RcDropped(RcReconnect.Entry e, bool left = false) =>
        Notify($"{e.Folder}: Remote Control dropped after the switch",
            left ? "Its input box did not read as expected after typing, so Enter was not pressed. Check that window."
                : "Type /remote-control in that window, or reconnect it from here.",
            ToolTipIcon.Warning, left ? null : ("Reconnect Remote Control", () => _ = RcManualAsync(e)));

    /// The toast's button: the same helper, without waiting for the user to be away (they just clicked).
    async Task RcManualAsync(RcReconnect.Entry e)
    {
        try
        {
            if (await Task.Run(() => RemoteControl.ConnectedSince(e.Transcript, e.Mark, e.NewUuid))) { Notify("", $"{e.Folder}: Remote Control is connected.", ToolTipIcon.None); return; }
            var result = await ConsoleNudge.RunAsync(Environment.ProcessPath!, e.Pid, e.SessionId, manual: true);
            Program.Log($"remote control: {e.SessionId} {result} (asked)");
            if (result.StartsWith("sent ") || result == "sent") { _rc.Track(e, DateTime.UtcNow); return; }
            Notify("", $"{e.Folder}: could not type /remote-control ({result.Split(' ').ElementAtOrDefault(1) ?? "error"}). Type it in that window.", ToolTipIcon.Warning);
        }
        catch (Exception ex) { Program.Log(ex); }
    }
}
