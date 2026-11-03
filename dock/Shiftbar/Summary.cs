namespace Shiftbar;

/// The cross-account view the flyout header, the "Use best" action, the rows' pace hints and the tray tooltip read.
/// Pure: everything comes from the engine's `list --json` reading.
public static class Summary
{
    /// The tray's fallback switch line, until the engine's own is read.
    public const double DefaultLine = 97;

    /// Room: a reading, not excluded, and both the 5-hour and weekly windows below the switch line.
    public static bool HasRoom(Account a, double line) =>
        !a.Disabled && a.ShownUsage is { } u && (u.FiveHour?.Pct ?? 0) < line && (u.SevenDay?.Pct ?? 0) < line;

    /// When the next account frees up: among accounts at the line, the soonest moment all their full windows have reset;
    /// with none at the line, the soonest 5-hour reset ahead. Null when no reset time is known.
    public static DateTimeOffset? NextReset(IReadOnlyList<Account> accounts, double line, DateTimeOffset now)
    {
        var limited = accounts.Where(a => !a.Disabled && a.ShownUsage != null && !HasRoom(a, line))
            .Select(a => new[] { a.ShownUsage!.FiveHour, a.ShownUsage.SevenDay }.Where(w => w != null && w.Pct >= line)
                .Select(w => w!.ResetsAt).ToList())
            .Where(rs => rs.Count > 0 && rs.All(r => r > now))
            .Select(rs => rs.Max())
            .ToList();
        if (limited.Count > 0) return limited.Min();
        return accounts.Select(a => a.ShownUsage?.FiveHour?.ResetsAt).Where(r => r > now).Min();
    }

    /// "3 of 4 accounts have room · next reset in 1h 12m". Empty when there is nothing listed. An account with no reading
    /// yet is "unknown", never "at its limit".
    public static string Header(Snapshot? snap, double line, DateTimeOffset now)
    {
        if (snap is not { Accounts.Count: > 0 } s) return "";
        var room = s.Accounts.Count(a => HasRoom(a, line));
        var unknown = Unknown(s.Accounts);
        var text = s.Accounts.Count == 1
            ? room == 1 ? "1 account, room left" : unknown == 1 ? "1 account, usage unknown" : "1 account, at its limit"
            : $"{room} of {s.Accounts.Count} accounts have room" + (unknown > 0 ? $" · {unknown} unknown" : "");
        return NextReset(s.Accounts, line, now) is { } t ? $"{text} · next reset {Format.When(t, now)}" : text;
    }

    /// Enabled accounts with no reading at all (none now, no last good one).
    static int Unknown(IReadOnlyList<Account> accounts) => accounts.Count(a => !a.Disabled && a.ShownUsage == null);

    /// The account `cswap switch --strategy best` would move to, or null when none is better than the active one.
    /// Mirrors the engine's rule: enabled, read, and strictly more headroom (lower peak) than the active account; ties stay.
    public static Account? Best(IReadOnlyList<Account> accounts)
    {
        if (accounts.FirstOrDefault(a => a.Active) is not { Usage: not null } on) return null;
        var mine = ResumeStopped.Peak(on);
        return accounts.Where(a => !a.Active && !a.Disabled && a.UsageStatus == "ok" && a.Usage != null && ResumeStopped.Peak(a) < mine)
            .OrderBy(ResumeStopped.Peak).ThenBy(a => a.Number).FirstOrDefault();
    }

    /// The account a near-limit notice offers to switch to: only with auto-switch off, only when the active account crossed
    /// into near or past its limit, and only when a better account exists.
    public static Account? SwitchOffer(IReadOnlyList<QuotaAlert> alerts, IReadOnlyList<Account> accounts, bool autoSwitch) =>
        !autoSwitch && alerts.Any(a => a.Active && a.State != QuotaState.Normal) ? Best(accounts) : null;

    /// The notice after a manual switch: any warning the engine gave first (the toast shows about three lines), then one
    /// short line on what moved.
    public static string SwitchedText(string name, IReadOnlyList<string>? warnings) =>
        string.Concat((warnings ?? []).Select(w => w + "\n")) + $"Default-login windows and new ones use {name}; pinned terminals keep theirs.";

    /// One line on the weekly window's pace: "weekly on pace", "weekly ahead of pace", "weekly runs out Thu 15:40". Null
    /// when the engine gave no pace for it. `Warn`: it runs out before it resets.
    public static (string Text, bool Warn)? Pace(Account a, DateTimeOffset now)
    {
        if (a.ShownUsage?.SevenDay is not { } w) return null;
        if (w.WillLastToReset == false && w.ProjectedExhaustionAt is { } t && t > now && (w.ResetsAt is not { } r || t < r))
            return ($"weekly runs out {Format.When(t, now)}", true);
        // Will not last, with no exhaustion time ahead: already used up (the engine sets it to its reading's time), or a stale reading.
        if (w.WillLastToReset == false) return (w.Pct >= 100 ? "weekly used up" : "weekly runs short", true);
        if (w.WillLastToReset == true) return (w.AheadOfPace == true ? "weekly ahead of pace, lasts to reset" : "weekly on pace", false);
        if (w.AheadOfPace == true) return ("weekly ahead of pace", false);
        return w.AheadOfPace == false ? ("weekly on pace", false) : null;
    }

    /// The tray tooltip: a summary line, then every account compactly (active first), within Windows' 127 characters.
    /// Names shorten first; accounts that still do not fit become "+N more".
    public static string Tooltip(Snapshot? s, bool auto, double line = DefaultLine)
    {
        const int Max = 127;
        if (s is not { Accounts.Count: > 0 }) return "Shiftbar — no accounts yet";
        var unknown = Unknown(s.Accounts);
        var head = $"{s.Accounts.Count(a => HasRoom(a, line))} of {s.Accounts.Count} have room" + (unknown > 0 ? $" · {unknown} unknown" : "") + (auto ? " · auto-switch" : "");
        var rows = s.Accounts.OrderByDescending(a => a.Active).ThenBy(a => a.Number).ToList();
        foreach (var nameMax in new[] { 18, 12, 8 })
        {
            var lines = rows.Select(a => Line(a, nameMax)).ToList();
            var text = string.Join("\n", lines.Prepend(head));
            if (text.Length <= Max) return text;
        }
        var kept = head;
        var shown = 0;
        foreach (var l in rows.Select(a => Line(a, 8)))
        {
            // Room is kept for the "+N more" line whenever another account follows this one.
            if (kept.Length + 1 + l.Length + (shown + 1 < rows.Count ? 10 : 0) > Max) break;
            kept += "\n" + l;
            shown++;
        }
        return shown < rows.Count ? kept + $"\n+{rows.Count - shown} more" : kept;
    }

    /// Alias, else the email's name part, cut to `max` characters: how compact surfaces (tooltip, buttons) name an account.
    public static string Name(Account a, int max = 16)
    {
        var name = !string.IsNullOrEmpty(a.Alias) ? a.Alias : a.Email.Split('@')[0];
        return name.Length > max ? name[..(max - 1)] + "…" : name;
    }

    static string Line(Account a, int nameMax)
    {
        var name = Name(a, nameMax);
        var use = a.ShownUsage is { } u ? $"{u.FiveHour?.Pct ?? 0:0}% · {u.SevenDay?.Pct ?? 0:0}%" : "–";
        return $"{(a.Active ? "● " : "")}{name} {use}";
    }
}
