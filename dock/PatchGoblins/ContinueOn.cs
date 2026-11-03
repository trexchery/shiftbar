namespace PatchGoblins;

/// "Continue on <account>": a window stopped at a limit carries on in a new terminal, in its folder, on an account with
/// room, with the conversation resumed. The stopped window is never closed or killed; its owner closes it.
public static class ContinueOn
{
    /// The account to continue on: never the stopped window's own (the new window would share its transcript), else as
    /// `ResumeStopped.Target` picks for a stop at a limit (most room, not refused, a profile to resume in or the default login).
    public static int? Target(StoppedSession s, IReadOnlyList<Account> accounts, Func<int, bool> hasProfile, DateTimeOffset now,
        IReadOnlyDictionary<int, DateTimeOffset>? refusedUntil = null, IReadOnlyDictionary<(int Account, string Scope), DateTimeOffset>? scopedRefused = null) =>
        ResumeStopped.Target(s with { AtLimit = true }, [.. accounts.Where(a => a.Number != s.Account)], hasProfile,
            now: now, refusedUntil: refusedUntil, scopedRefused: scopedRefused);

    /// The engine arguments: `run N [--restore-rc] -- --resume <session> "A usage limit was hit. Continue."`.
    public static string[] Args(StoppedSession s, int account, bool restoreRc) =>
        Launch.Run(account, restoreRc, ResumeStopped.ResumeArgs(s with { AtLimit = true }));

    /// Where `--resume` must find the transcript for `account`: the default login's dir when it is the active one (`cswap
    /// run` then runs plain `claude`), else its profile; null when that profile does not exist yet.
    public static string? DirFor(int account, IReadOnlyList<Account> accounts) =>
        accounts.FirstOrDefault(a => a.Number == account)?.Active == true ? ResumeStopped.DefaultDir : ResumeStopped.ProfileDir(account);

    /// Copies the conversation where the new window resumes it, then opens that window. `remember`: the limit error the
    /// copy carries, so the watcher does not take it for a new refusal of the target account.
    public static void Start(Engine engine, StoppedSession s, Account target, string dir, bool restoreRc, Action<DateTimeOffset> remember)
    {
        if (s.Limit is { } l && l.At != default) remember(l.At);
        ResumeStopped.CopyTranscript(s, dir);
        engine.LaunchConsole($"Claude · {target.ShortName} · continued", s.Cwd, Args(s, target.Number, restoreRc));
    }
}
