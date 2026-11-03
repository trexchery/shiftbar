namespace PatchGoblins;

/// The live sessions: the flyout's Sessions list, the notice when one stops at a limit, and "Continue on <account>".
public sealed partial class TrayApp
{
    List<LiveSession> _sessions = [];

    async Task MovePassAsync(Snapshot snap, bool recover)
    {
        List<StoppedSession> stopped;
        List<LiveSession> live;
        try
        {
            (stopped, live) = await Task.Run(() =>
                (ScanStops(snap, includeBusy: true), ResumeStopped.LiveSessions(ResumeStopped.ConfigDirs(snap.ActiveAccountNumber))));
        }
        catch (Exception e) { Program.Log(e); return; }
        if (_engineThreshold == null) await ReadEngineThresholdAsync();
        // A stop already continued elsewhere offers no second Continue: its window only waits to be closed.
        _sessions = [.. Sessions.Merge(live, stopped).Select(s => s.AtLimit && !Continued(s.Stop!) ? s with { ContinueOn = ContinueTarget(s.Stop!, snap) } : s)];
        foreach (var s in _sessions.Where(s => s.AtLimit)) TellStop(s, snap);
        if (recover) await RecoverOrphansAsync(snap, stopped.Where(s => s.Orphan).ToList());
    }

    /// Whether this stop was continued: the session reopened since its limit error. The continued window keeps the session id,
    /// so a later stop of its own is a new one.
    bool Continued(StoppedSession s) =>
        _resumed.Any(m => m.Key.StartsWith(s.SessionId + "#", StringComparison.Ordinal) && m.Value >= (s.Limit?.At.UtcDateTime ?? DateTime.MinValue));

    int? ContinueTarget(StoppedSession s, Snapshot snap) =>
        ContinueOn.Target(s, snap.Accounts, n => ResumeStopped.ProfileDir(n) != null, DateTimeOffset.UtcNow, _limits.RefusedUntil, _limits.ScopedRefusedUntil);

    /// Once per stop: "<folder> stopped at <account>'s limit", with "Continue on <best>" when an account has room.
    void TellStop(LiveSession s, Snapshot snap)
    {
        var stop = s.Stop!;
        if (!_told.Add($"{s.SessionId}@{stop.Limit?.ResetsAt ?? stop.Limit?.At}")) return;
        var folder = Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
        var own = snap.Accounts.FirstOrDefault(a => a.Number == s.Account);
        var title = own != null ? $"{folder} stopped at {own.ShortName}'s limit" : $"{folder} stopped at a usage limit";
        if (s.ContinueOn is { } n && snap.Accounts.FirstOrDefault(a => a.Number == n) is { } target)
            Notify(title, $"Continue it on {target.ShortName} in a new terminal, conversation resumed. This window stays open until you close it.",
                ToolTipIcon.Warning, ($"Continue on {Summary.Name(target, 14)}", () => _ = ContinueAsync(stop)));
        else
            Notify(title, stop.Limit?.ResetsAt is { } r ? $"No other account has room yet. It resets {Format.When(r, DateTimeOffset.Now)}." : "No other account has room yet.",
                ToolTipIcon.Warning);
    }

    /// Opens a new terminal in the stopped session's folder on the best account with room, the conversation resumed. The
    /// stopped window is left exactly as it is: nothing here closes or signals a process.
    async Task ContinueAsync(StoppedSession s)
    {
        if (_snap is not { } snap) return;
        var now = DateTime.UtcNow;
        var folder = Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));
        if (Continued(s)) return; // a second click (toast and row): one new window per stop
        if (ContinueTarget(s, snap) is not { } n || snap.Accounts.FirstOrDefault(a => a.Number == n) is not { } target
            || ContinueOn.DirFor(n, snap.Accounts) is not { } dir)
        {
            Notify("No account with room", $"{folder} cannot continue anywhere yet.", ToolTipIcon.Warning);
            return;
        }
        try { await Task.Run(() => ContinueOn.Start(_engine, s, target, dir, _settings.PreserveRemoteControl, at => _limitWatch.Remember(at))); }
        catch (Exception e) { Error("Could not continue the session", e); return; }
        _resumed[$"{s.SessionId}#{n}"] = now; // the default login does not switch onto it while the new window starts
        ResumeStopped.SaveMoves(MovesPath, _resumed, now);
        _limits.Forget(s.SessionId);
        Notify("", $"{folder} continues on {target.ShortName}. Close the stopped window when you like.", ToolTipIcon.None);
        _sessions = [.. _sessions.Select(x => x.SessionId == s.SessionId ? x with { ContinueOn = null } : x)];
        if (_dash.Visible) RenderDash();
    }

    /// A terminal in the session's folder on the account it runs as (the default login's window: the default login).
    void OpenSessionTerminal(LiveSession s)
    {
        if (!Directory.Exists(s.Cwd)) { Notify("Folder not found", s.Cwd, ToolTipIcon.Warning); return; }
        if (s.Account is { } n && _snap?.Accounts.FirstOrDefault(a => a.Number == n) is { } account) { OpenTerminal(account, s.Cwd); return; }
        try { _engine.LaunchConsole("Claude · default login", s.Cwd, Launch.Run(null, _settings.PreserveRemoteControl)); }
        catch (Exception e) { Error("Could not open terminal", e); }
    }
}
