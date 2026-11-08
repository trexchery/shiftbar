using System.Diagnostics;

namespace Shiftbar;

/// The browser half of a switch on macOS: `open` to launch a profile, System Events to close one's windows.
/// Every command is an argv list (no shell); the profile name reaches AppleScript as an argument, never as script text.
internal sealed class MacBrowserHost(BrowserKind kind, string app, Action<string> log,
    Func<string, IReadOnlyList<string>, (int Code, string Out, string Err)>? run = null) : IBrowserHost
{
    const string AccessibilityHint = "Allow Shiftbar in System Settings → Privacy & Security → Accessibility to close old browser windows";
    readonly Func<string, IReadOnlyList<string>, (int Code, string Out, string Err)> run = run ?? Run;
    bool told;

    // The main executable's full path: Chrome and Canary differ only there, and helpers live under Frameworks.
    public bool Running() => run("/usr/bin/pgrep", ["-f", "^" + (app + "/Contents/MacOS/" + kind.Name).Replace(".", "\\.")]).Code == 0;

    public void Launch(string profileDir)
    {
        var (code, _, error) = run("/usr/bin/open", ["-na", app, "--args", "--profile-directory=" + profileDir]);
        if (code != 0) throw new InvalidOperationException($"open failed ({code}): {error.Trim()}");
    }

    // Only the window's toolbars are searched, never the page (AXWebArea): a page in the new account's window may show a
    // button named after the old profile ("Continue as <email>").
    // ponytail: unverified on a real Mac that the avatar button sits in an AXToolbar with the profile name as its name or
    // description; if it does not, nothing closes (never the wrong window). Check Chrome's AX tree and adjust the path.
    static readonly string[] CloseScript =
    [
        "on run argv",
        "set wanted to item 1 of argv",
        "set closed to 0",
        "tell application \"System Events\" to tell process (item 2 of argv)",
        "repeat with w in (get every window)",
        "set hit to false",
        "repeat with t in (get toolbars of w)",
        "repeat with e in (get entire contents of t)",
        "try",
        "if class of e is button and (name of e is wanted or description of e is wanted) then set hit to true",
        "end try",
        "if hit then exit repeat",
        "end repeat",
        "if hit then exit repeat",
        "end repeat",
        "if hit then",
        "click (first button of w whose subrole is \"AXCloseButton\")",
        "set closed to closed + 1",
        "end if",
        "end repeat",
        "end tell",
        "return closed",
        "end run",
    ];

    public int CloseWindows(string profileName)
    {
        var (code, output, error) = run("/usr/bin/osascript", [.. CloseScript.SelectMany(line => new[] { "-e", line }), profileName, kind.Name]);
        if (code == 0) return int.TryParse(output.Trim(), out var n) ? n : 0;
        // -1719 / -25211: System Events refused because Shiftbar lacks Accessibility.
        if (error.Contains("assistive", StringComparison.OrdinalIgnoreCase) || error.Contains("-1719") || error.Contains("-25211"))
        {
            if (!told) { told = true; log(AccessibilityHint); }
        }
        else log($"browser: osascript failed ({code}): {error.Trim()}");
        return 0;
    }

    static (int, string, string) Run(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, "", "timed out"); }
        return (p.ExitCode, output.Result, error.Result);
    }
}
