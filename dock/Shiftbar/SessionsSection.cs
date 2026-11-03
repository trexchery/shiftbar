namespace Shiftbar;

/// The flyout's Sessions list: pure helpers for what each row says, plus the row itself.
public static class Sessions
{
    /// Rows shown before the rest collapse into "+N more".
    public const int Shown = 5;

    /// The scan's stops laid over the live windows (same process, same session).
    public static List<LiveSession> Merge(IEnumerable<LiveSession> live, IEnumerable<StoppedSession> stops)
    {
        var byKey = stops.GroupBy(s => (s.Pid, s.SessionId)).ToDictionary(g => g.Key, g => g.First());
        return [.. live.Select(l => byKey.TryGetValue((l.Pid, l.SessionId), out var s) ? l with { Stop = s } : l)];
    }

    /// Stopped at a limit first, then the newest.
    public static List<LiveSession> Order(IEnumerable<LiveSession> sessions) =>
        [.. sessions.OrderByDescending(s => s.AtLimit).ThenByDescending(s => s.StartedUtc).ThenBy(s => s.SessionId, StringComparer.Ordinal)];

    public static string State(LiveSession s, DateTimeOffset now) => s switch
    {
        { AtLimit: true } => s.Stop!.Limit?.ResetsAt is { } r && r > now ? $"stopped at limit · resets {Format.When(r, now)}" : "stopped at limit",
        { Stop.Orphan: true } => "terminal closed",
        { Status: "busy" or "shell" } => "running",
        { Status: "idle" } => "idle",
        { Status: "waiting" } => "waiting for you",
        { Status.Length: > 0 } => s.Status,
        _ => "open",
    };

    /// "just now", "12m", "3h", "2d".
    public static string Age(TimeSpan d) =>
        d < TimeSpan.FromMinutes(1) ? "just now" : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes}m"
        : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalDays}d";

    /// The folder as a row shows it: the home folder as "~", and a long path cut to its last two folders ("…\repo\src").
    public static string ShortFolder(string cwd, string? home = null, int max = 28)
    {
        var dir = cwd.TrimEnd('\\', '/');
        if (dir.Length == 0) return cwd;
        var sep = dir.Contains('\\') ? '\\' : '/';
        if (home?.TrimEnd('\\', '/') is { Length: > 0 } h && dir.StartsWith(h, StringComparison.OrdinalIgnoreCase)
            && (dir.Length == h.Length || dir[h.Length] is '\\' or '/'))
            dir = "~" + dir[h.Length..];
        if (dir.Length <= max) return dir;
        var parts = dir.Split('\\', '/');
        if (parts.Length >= 2 && $"…{sep}{parts[^2]}{sep}{parts[^1]}" is { } two && two.Length <= max) return two;
        var leaf = $"…{sep}{parts[^1]}";
        return leaf.Length <= max ? leaf : parts[^1][..(max - 1)] + "…";
    }
}

/// One live session in the flyout: its folder, then the account it runs as, its state and age. Actions on the right:
/// a terminal in its folder on the same account, and for a stop at a limit "Continue on <account>".
public sealed class SessionRow : Control
{
    public event Action<LiveSession>? TerminalClicked, ContinueClicked;

    static readonly Font NameFont = Ui.Font(8.5f, semibold: true), MetaFont = Ui.Font(8f);
    readonly LiveSession _s;
    readonly string _folder, _meta;
    readonly Color _dot;
    readonly Pill _terminal;
    readonly Pill? _continue;
    readonly ToolTip _tip = new();

    public LiveSession Session => _s;
    internal Pill? ContinueButton => _continue;

    /// `account`: the one it runs as; `stoppedOn`: the one whose limit stopped it (a default-login window's is the login it
    /// had then, not the one it runs as after a switch).
    public SessionRow(LiveSession s, Account? account, Account? continueOn, DateTimeOffset now, Account? stoppedOn = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _s = s;
        Width = Ui.S(AccountRow.BaseWidth);
        Height = Ui.S(44);
        Margin = new Padding(0, 0, 0, Ui.S(2));
        var pal = Palette.Current;
        _folder = Sessions.ShortFolder(s.Cwd, StoreSecurity.UserProfile);
        var named = s.AtLimit ? stoppedOn : account;
        var who = named != null ? Summary.Name(named) : (s.AtLimit ? s.Stop!.Account : s.Account) is { } n ? $"#{n}" : "default login";
        // A stop says when it resets; its age would read as part of that, so only the others show one.
        _meta = $"{who} · {Sessions.State(s, now)}" + (s.AtLimit ? "" : $" · {Sessions.Age(now.UtcDateTime - s.StartedUtc)}");
        _dot = named?.ShownUsage != null ? pal.Meter(ResumeStopped.Peak(named)) : pal.Faint;
        _tip.SetToolTip(this, s.Cwd);

        _terminal = new Pill("", PillStyle.Icon, Ui.Terminal) { BackColor = pal.Back };
        _terminal.Location = new Point(Width - Ui.S(12) - _terminal.Width, (Height - _terminal.Height) / 2);
        _terminal.Click += (_, _) => TerminalClicked?.Invoke(_s);
        _tip.SetToolTip(_terminal, account != null ? $"Terminal in this folder as {account.ShortName}" : "Terminal in this folder");
        Controls.Add(_terminal);
        if (s.AtLimit && continueOn != null)
        {
            // The primary action on a line of its own under the text, as an expanded account row has its buttons.
            Height = Ui.S(74);
            _continue = new Pill($"Continue on {Summary.Name(continueOn, 16)}", PillStyle.Primary) { BackColor = pal.Back };
            _continue.Font = Ui.Font(8f, semibold: true);
            _continue.Height = Ui.S(26);
            _continue.Width = TextRenderer.MeasureText(_continue.Text, _continue.Font).Width + Ui.S(20);
            _continue.Location = new Point(Ui.S(26), Ui.S(42));
            _continue.Click += (_, _) => ContinueClicked?.Invoke(_s);
            _tip.SetToolTip(_continue, $"Open a new terminal in this folder on {continueOn.ShortName} with the conversation resumed. This window stays open.");
            Controls.Add(_continue);
        }
    }

    int TextRight => _terminal.Left - Ui.S(8);

    protected override void OnPaint(PaintEventArgs e)
    {
        var pal = Palette.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? pal.Back);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int pad = Ui.S(12), x = pad + Ui.S(14);
        using (var dot = new SolidBrush(_dot)) g.FillEllipse(dot, pad, Ui.S(13), Ui.S(6), Ui.S(6));
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var w = TextRight - x;
        TextRenderer.DrawText(g, _folder, NameFont, new Rectangle(x, Ui.S(6), w, NameFont.Height), pal.Text, flags);
        TextRenderer.DrawText(g, _meta, MetaFont, new Rectangle(x, Ui.S(24), w, MetaFont.Height), _s.AtLimit ? Palette.Warn : pal.Muted, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
