namespace Shiftbar;

/// "Continue on <account>": a window stopped at a limit carries on in a new terminal, in its folder, on an account with
/// room, with the conversation resumed. The stopped window is never closed or killed; its owner closes it.
public static class ContinueOn
{
    /// The account to continue on, as `ResumeStopped.Target` picks for a stop at a limit (most room, not refused). Never the
    /// stopped window's own account, and never one whose folder is the stopped window's own or has a live claude on this
    /// session (`holdsSession`): two windows on one transcript would collide. `dirFor`: where that account resumes (`DirFor`).
    public static int? Target(StoppedSession s, IReadOnlyList<Account> accounts, Func<int, string?> dirFor, Func<string, bool> holdsSession, DateTimeOffset now,
        IReadOnlyDictionary<int, DateTimeOffset>? refusedUntil = null, IReadOnlyDictionary<(int Account, string Scope), DateTimeOffset>? scopedRefused = null)
    {
        bool Apart(Account a) => a.Number != s.Account && dirFor(a.Number) is { } d && !ResumeStopped.SameDir(d, s.ConfigDir) && !holdsSession(d);
        return ResumeStopped.Target(s with { AtLimit = true }, [.. accounts.Where(Apart)], _ => true,
            now: now, refusedUntil: refusedUntil, scopedRefused: scopedRefused);
    }

    /// The engine arguments: `run N [--restore-rc] -- --resume <session> "A usage limit was hit. Continue." [--remote-control]`,
    /// the last when the stopped session itself had Remote Control (`RemoteControl.WithRc`).
    public static string[] Args(StoppedSession s, int account, bool restoreRc) =>
        Launch.Run(account, restoreRc, RemoteControl.WithRc(ResumeStopped.ResumeArgs(s with { AtLimit = true }), s.Transcript, restoreRc));

    /// Where `--resume` must find the transcript for `account`: the default login's dir when it is the active one (`cswap
    /// run` then runs plain `claude`), else its profile, or where `cswap run` creates it on the first run. Null: not listed.
    public static string? DirFor(int account, IReadOnlyList<Account> accounts) =>
        accounts.FirstOrDefault(a => a.Number == account) is not { } a ? null
        : a.Active ? ResumeStopped.DefaultDir : ResumeStopped.ProfileDir(account) ?? ResumeStopped.NewProfileDir(a);

    /// Copies the conversation where the new window resumes it, then opens that window. `remember`: the limit error the
    /// copy carries, so the watcher does not take it for a new refusal of the target account.
    public static void Start(Engine engine, StoppedSession s, Account target, string dir, bool restoreRc, Action<DateTimeOffset> remember)
    {
        if (s.Limit is { } l && l.At != default) remember(l.At);
        ResumeStopped.CopyTranscript(s, dir);
        engine.LaunchConsole(RemoteControl.TitleOf(s.Transcript) ?? $"Claude · {target.ShortName} · continued", s.Cwd, Args(s, target.Number, restoreRc));
    }
}
