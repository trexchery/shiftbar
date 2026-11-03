namespace PatchGoblins;

/// The live sessions: the flyout's Sessions list, the notice when one stops at a limit, and "Continue on <account>".
public sealed partial class TrayApp
{
    List<LiveSession> _sessions = [];
    readonly HashSet<string> _continuing = []; // session ids a Continue is copying and launching for right now

    async Task MovePassAsync(Snapshot snap, bool recover)
    {
        List<StoppedSession> stopped;
        List<LiveSession> live;
        (int? Account, DateTime ChangedAt) login;
        try
        {
            (stopped, live, login) = await Task.Run(() =>
                (ScanStops(snap, includeBusy: true), ResumeStopped.LiveSessions(ResumeStopped.ConfigDirs(snap.ActiveAccountNumber)), ResumeStopped.DefaultLogin()));
        }
        catch (Exception e) { Program.Log(e); return; }
        if (_engineThreshold == null) await ReadEngineThresholdAsync();
        // A default-login window is listed as today's login, but its stop is the account that refused it: after a switch that is
        // the old one. The login's record is trusted only while it agrees with the listing, as in ScanStops.
        var trusted = login.Account == snap.ActiveAccountNumber ? login : (snap.ActiveAccountNumber, DateTime.MaxValue);
        var stops = stopped.Select(s => s is { AtLimit: true, Limit: { } l } && s.ConfigDir == ResumeStopped.DefaultDir
            ? s with { Account = ResumeStopped.Refuser(l, null, snap.Accounts, trusted) } : s);
        // A stop already continued elsewhere offers no second Continue: its window only waits to be closed.
        _sessions = [.. Sessions.Merge(live, stops).Select(s => s.AtLimit && !Continued(s.Stop!) ? s with { ContinueOn = ContinueTarget(s.Stop!, snap) } : s)];
        foreach (var s in _sessions.Where(s => s.AtLimit && !Continued(s.Stop!))) TellStop(s, snap);
        if (recover) await RecoverOrphansAsync(snap, stopped.Where(s => s.Orphan).ToList());
    }

    /// Whether this stop was continued: the session reopened since its limit error. The continued window keeps the session id,
    /// so a later stop of its own is a new one.
    bool Continued(StoppedSession s) =>
        _resumed.Any(m => m.Key.StartsWith(s.SessionId + "#", StringComparison.Ordinal) && m.Value >= (s.Limit?.At.UtcDateTime ?? DateTime.MinValue));

    int? ContinueTarget(StoppedSession s, Snapshot snap) => InPlace(s, snap) != null ? null
        : ContinueOn.Target(s, snap.Accounts, n => ContinueOn.DirFor(n, snap.Accounts), d => ResumeStopped.HoldsSession(d, s.SessionId),
            DateTimeOffset.UtcNow, _limits.RefusedUntil, _limits.ScopedRefusedUntil);

    /// The account a default-login window carries on with where it is: the login moved off the account that stopped it onto
    /// one with room, so its next message runs there. A second window on the same transcript would only collide with it.
    Account? InPlace(StoppedSession s, Snapshot snap) =>
        s.ConfigDir == ResumeStopped.DefaultDir && snap.Accounts.FirstOrDefault(a => a.Active) is { } on && on.Number != s.Account && Summary.HasRoom(on, Threshold)
            ? on : null;

    /// Once per stop: "<folder> stopped at <account>'s limit", with "Continue on <best>" when an account has room.
    void TellStop(LiveSession s, Snapshot snap)
    {
        var stop = s.Stop!;
        if (!_told.Add($"{s.SessionId}@{stop.Limit?.ResetsAt ?? stop.Limit?.At}")) return;
        var folder = Folder(stop);
        var own = snap.Accounts.FirstOrDefault(a => a.Number == stop.Account);
        var title = own != null ? $"{folder} stopped at {own.ShortName}'s limit" : $"{folder} stopped at a usage limit";
        if (s.ContinueOn is { } n && snap.Accounts.FirstOrDefault(a => a.Number == n) is { } target)
            Notify(title, $"Continue it on {target.ShortName} in a new terminal, conversation resumed. This window stays open until you close it.",
                ToolTipIcon.Warning, ($"Continue on {Summary.Name(target, 14)}", () => _ = ContinueAsync(stop, n)));
        else if (InPlace(stop, snap) is { } on)
            Notify(title, $"The default login is now on {on.ShortName}: type continue in that window to carry on.", ToolTipIcon.Warning);
        else
            Notify(title, stop.Limit?.ResetsAt is { } r ? $"No other account has room yet. It resets {Format.When(r, DateTimeOffset.Now)}." : "No other account has room yet.",
                ToolTipIcon.Warning);
    }

    static string Folder(StoppedSession s) => Path.GetFileName(s.Cwd.TrimEnd('\\', '/'));

    /// Opens a new terminal in the stopped session's folder on the best account with room, the conversation resumed. The
    /// stopped window is left exactly as it is: nothing here closes or signals a process. `offered`: the account the button
    /// named; when the best one changed since, that is offered instead of launching somewhere the user did not pick.
    async Task ContinueAsync(StoppedSession s, int? offered)
    {
        if (_snap is not { } snap || Continued(s)) return; // a second click (toast and row): one new window per stop
        if (!_continuing.Add(s.SessionId)) return; // the first click is still copying
        try
        {
            var now = DateTime.UtcNow;
            var folder = Folder(s);
            if (InPlace(s, snap) is { } on)
            {
                Notify("", $"The default login is now on {on.ShortName}: type continue in the {folder} window to carry on.", ToolTipIcon.None);
                return;
            }
            if (ContinueTarget(s, snap) is not { } n || snap.Accounts.FirstOrDefault(a => a.Number == n) is not { } target
                || ContinueOn.DirFor(n, snap.Accounts) is not { } dir)
            {
                Notify("No account with room", $"{folder} cannot continue anywhere yet.", ToolTipIcon.Warning);
                return;
            }
            if (offered is { } o && o != n)
            {
                Notify("", $"{target.ShortName} now has the most room for {folder}.", ToolTipIcon.None,
                    ($"Continue on {Summary.Name(target, 14)}", () => _ = ContinueAsync(s, n)));
                return;
            }
            // Right before the launch: still stopped at that limit, by the same claude. Someone may have typed in it since.
            if (!await Task.Run(() => ResumeStopped.StillStopped(s)))
            {
                Notify("", $"{folder} is no longer stopped at its limit: nothing to continue.", ToolTipIcon.None);
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
        finally { _continuing.Remove(s.SessionId); }
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
