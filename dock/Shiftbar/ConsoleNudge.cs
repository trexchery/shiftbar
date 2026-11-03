using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Shiftbar;

/// `shiftbar rc-nudge <pid> <session> [--manual]`: types `/remote-control` into a Claude Code window, only when that is
/// provably harmless, and prints one result line. Runs as its own short-lived process (the tray starts it), because attaching
/// to another console is per process. Every doubt types nothing:
/// 1. the window is idle and the user away (`RemoteControl.Ready`; `--manual`, the toast's button, skips only the away check);
/// 2. its screen shows Claude Code's input box (a '─' rule, a '>'/'❯' prompt line, a closing rule) with nothing after the marker;
/// 3. the text goes in as one `WriteConsoleInputW`; Enter follows, as its own write, only once the box reads exactly
///    `/remote-control` and the gates still hold. Otherwise our characters are taken out again with backspaces when the box holds
///    exactly them, and left alone when it holds anything else. No Ctrl, Esc or other key is ever sent.
/// Results: `sent`, `refused <why>` (nothing typed), `aborted <why>` (typed, then removed), `left <why>` (typed, box not as
/// expected, nothing more done), `error <why>`. `rc-nudge --probe <pid>` reads the box only and prints its shape, never its text
/// (shiftbar.exe is a GUI program: a shell shows its line only when piped, e.g. `shiftbar.exe rc-nudge --probe 1234 | Out-String`).
public static class ConsoleNudge
{
    public const string Command = "/remote-control";
    /// Input rows a box may span, and non-blank rows allowed under an empty one (status line, hints): more is a menu or a dialog.
    const int MaxInputRows = 12, MaxBelow = 6;

    public enum BoxState { Empty, Text, Unrecognised }

    /// What the screen shows: `Text` is what follows the prompt marker (continuation rows joined by '\n'), `Chars` its
    /// non-blank characters; `Why` describes the box's shape without any of its text.
    public sealed record Box(BoxState State, string Why, string Text = "", int Chars = 0);

    /// Under the box: a running turn's hint, a dialog or a menu. Above it only the spinner's hint counts: those rows are
    /// usually the end of the last reply, whose words prove nothing.
    static readonly Regex Busy = new(@"esc to interrupt|esc to cancel|do you want|❯\s*\d+\.", RegexOptions.IgnoreCase),
        Running = new(@"esc to interrupt|esc to cancel", RegexOptions.IgnoreCase);

    /// Finds Claude Code's input box, the bottom-most '─' rule directly above a prompt line, and reads it. `attrs` (each row's
    /// cell attributes) may prove the text after the marker is a dimmed placeholder; without that proof any text is text.
    /// `typed`: read after our typing, when an autocomplete list may sit under the box. `cursorRow` (the console cursor's row
    /// in `rows`): a box above it on screen is the frame being drawn only when the cursor is at or under its top rule.
    public static Box Parse(IReadOnlyList<string> rows, int width, IReadOnlyList<ushort[]?>? attrs = null, bool typed = false, int? cursorRow = null)
    {
        var raw = rows.Select(r => r.Replace('\0', ' ')).ToList();
        var r = raw.Select(x => x.TrimEnd()).ToList();
        if (r.Count == 0 || width <= 0) return new(BoxState.Unrecognised, "empty screen");
        var top = -1;
        for (var i = r.Count - 1; i >= 1 && top < 0; i--) if (MarkerAt(r[i]) >= 0 && IsRule(r[i - 1], width)) top = i - 1;
        if (top < 0) return new(BoxState.Unrecognised, $"no rule followed by a prompt line in {r.Count} rows (width {width})");
        if (cursorRow is { } c && (c < top || c >= r.Count))
            return new(BoxState.Unrecognised, $"cursor row {c + 1} is not at or under the box at row {top + 1} of {r.Count} (an old frame?)");
        var prompt = top + 1;
        var end = -1;
        for (var j = prompt + 1; j < Math.Min(r.Count, prompt + 1 + MaxInputRows) && end < 0; j++) if (IsRule(r[j], width)) end = j;
        if (end < 0) return new(BoxState.Unrecognised, $"prompt line at row {prompt + 1} of {r.Count} has no closing rule");
        var above = r.Skip(Math.Max(0, top - 3)).Take(Math.Min(3, top));
        var under = r.Skip(end + 1).ToList();
        if (above.Any(Running.IsMatch) || !typed && under.Any(Busy.IsMatch)) return new(BoxState.Unrecognised, "a running turn, menu or dialog is showing");
        var below = under.Count(x => x.Trim().Length > 0);
        if (!typed && below > MaxBelow) return new(BoxState.Unrecognised, $"{below} rows below the box (a menu?)");
        var marker = MarkerAt(r[prompt]);
        var lines = new[] { r[prompt][(marker + 1)..] }.Concat(r.Skip(prompt + 1).Take(end - prompt - 1)).Select(x => x.Trim()).ToList();
        while (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var text = string.Join('\n', lines);
        var chars = text.EnumerateRunes().Count(c => !Rune.IsWhiteSpace(c));
        var shape = $"prompt-line found at row {prompt + 1} of {r.Count} (width {width}), marker U+{(int)r[prompt][marker]:X4}, {end - prompt - 1} input row(s), " +
                    $"{chars} chars after marker, {below} row(s) below the box";
        if (chars == 0) return new(BoxState.Empty, shape);
        if (!typed && Placeholder(raw[prompt], attrs?.ElementAtOrDefault(prompt), marker, width, lines.Count))
            return new(BoxState.Empty, shape + ", a dimmed placeholder");
        return new(BoxState.Text, shape + (attrs?.ElementAtOrDefault(prompt) is { } a && a.Length > marker + 2 ? $", attr 0x{a[marker + 2]:X4} after marker" : ""), text, chars);
    }

    /// A placeholder is only proven when it fills one row of narrow characters (cells and characters line up) and every cell
    /// of it but the first (Claude Code's drawn cursor) is dark grey on the default background.
    static bool Placeholder(string row, ushort[]? attrs, int marker, int width, int rows)
    {
        if (attrs == null || rows != 1 || row.Length != width || attrs.Length != width || row.Any(char.IsSurrogate)) return false;
        var cells = Enumerable.Range(marker + 1, width - marker - 1).Where(i => row[i] != ' ').Skip(1).ToList();
        return cells.Count > 0 && cells.All(i => (attrs[i] & 0xFF) == 0x08);
    }

    /// Where a prompt line's marker is ('>' or '❯' after at most two spaces, then a space or nothing), else -1.
    static int MarkerAt(string row)
    {
        var i = 0;
        while (i < row.Length && i < 2 && row[i] == ' ') i++;
        return i < row.Length && (row[i] is '>' or '❯') && (i + 1 == row.Length || row[i + 1] == ' ') ? i : -1;
    }

    /// A full-width '─' rule. Claude Code 2.1.317 labels the box's top rule near its right end ("───…─── auto-mode ─"), so a
    /// rule may carry a label in its last quarter, after an unbroken run of '─' over three quarters of the width, and must
    /// end with '─'. A rule broken earlier (or a row that merely contains dashes) is no rule.
    static bool IsRule(string row, int width)
    {
        var t = row.Trim();
        var lead = t.TakeWhile(c => c == '─').Count();
        return t.Length >= Math.Max(8, width / 2) && t[^1] == '─' && (lead == t.Length || lead >= Math.Max(8, width * 3 / 4));
    }

    /// The probe's redacted outline of the last 20 drawn rows: R rule, P prompt line, t anything else, . blank.
    public static string Shape(IReadOnlyList<string> rows, int width)
    {
        var s = string.Concat(rows.Select(x => x.Replace('\0', ' ').TrimEnd()).Select(x => x.Length == 0 ? '.' : IsRule(x, width) ? 'R' : MarkerAt(x) >= 0 ? 'P' : 't')).TrimEnd('.');
        return s[Math.Max(0, s.Length - 20)..];
    }

    /// The helper process's entry (Program routes `rc-nudge` here before anything else starts). Never throws.
    public static int Run(string[] args)
    {
        var stdout = Console.OpenStandardOutput(); // taken before attaching, so the line reaches the tray's pipe
        string line;
        try
        {
            line = !OperatingSystem.IsWindows() ? "error not-windows" : args switch
            {
                ["--probe", var p] when int.TryParse(p, out var pid) => Probe(pid),
                [var p, var sid, .. var rest] when int.TryParse(p, out var pid) && rest.All(a => a == "--manual") => Nudge(pid, sid, rest.Length > 0),
                _ => "error usage: rc-nudge <pid> <session-id> [--manual] | rc-nudge --probe <pid>",
            };
        }
        catch (Exception e) { line = "error " + e.GetType().Name; }
        finally { FreeConsole(); }
        try { stdout.Write(Encoding.UTF8.GetBytes(line + "\n")); stdout.Flush(); } catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { }
        return line.StartsWith("sent") || line.StartsWith("probe") ? 0 : 1;
    }

    static string Probe(int pid)
    {
        using var con = Attached.Open(pid, out var why);
        if (con == null) return "probe not-recognised: " + why;
        if (con.Read() is not { } s) return "probe not-recognised: the screen buffer could not be read";
        var box = Parse(s.Rows, s.Width, s.Attrs, cursorRow: s.CursorRow);
        var state = box.State switch { BoxState.Empty => "empty", BoxState.Text => "not-empty", _ => "not-recognised" };
        return $"probe {state}: {box.Why}; window {s.Width}x{s.Rows.Count}, cursor row {s.CursorRow + 1}; shape {Shape(s.Rows, s.Width)}";
    }

    static string Nudge(int pid, string sid, bool manual)
    {
        var dir = ResumeStopped.DefaultDir;
        if (RemoteControl.Ready(dir, pid, sid, manual) is { } busy) return "refused " + busy;
        using var con = Attached.Open(pid, out var why);
        if (con == null) return "refused " + why;
        if (con.Read() is not { } s) return "refused unreadable";
        var box = Parse(s.Rows, s.Width, s.Attrs, cursorRow: s.CursorRow);
        if (box.State != BoxState.Empty) return "refused " + (box.State == BoxState.Text ? "not-empty" : "unrecognised");
        if (RemoteControl.Ready(dir, pid, sid, manual) is { } late) return "refused " + late; // right before typing
        var written = con.Type(Command);
        if (written == 0) return "refused write-failed";
        var after = WaitFor(con, b => b.Text == Command);
        if (written != Command.Length * 2 || after?.Text != Command)
            return after?.State == BoxState.Empty && con.Pending() ? "left pending-input"
                : "left " + (after == null ? "unreadable" : after.Text.StartsWith(Command) ? "mismatch-after-command" : "mismatch");
        if (RemoteControl.Ready(dir, pid, sid, manual) is { } changed) return Remove(con, changed);
        // Ready reads files for a while: the box is read once more, with nothing slow between this and Enter.
        if (con.Read() is not { } last) return "left unreadable";
        if (Parse(last.Rows, last.Width, typed: true).Text != Command) return "left mismatch";
        return con.Key('\r', 0x0D) ? "sent" : Remove(con, "enter-failed");
    }

    /// Takes our characters out of a box that holds exactly them.
    static string Remove(Attached con, string why)
    {
        con.Backspaces(Command.Length);
        return WaitFor(con, b => b.State == BoxState.Empty)?.State == BoxState.Empty ? "aborted " + why : "left " + why;
    }

    /// Re-reads the box (as typed) until `done` or 3 s; the last reading, null when unreadable.
    static Box? WaitFor(Attached con, Func<Box, bool> done)
    {
        Box? last = null;
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 3000; Thread.Sleep(100))
        {
            if (con.Read() is not { } s) return null;
            last = Parse(s.Rows, s.Width, typed: true);
            if (done(last)) return last;
        }
        return last;
    }

    /// Starts the helper (the tray's own exe) and returns its result line; never throws. `env`: extra variables (tests).
    public static async Task<string> RunAsync(string exe, int pid, string sessionId, bool manual, IReadOnlyDictionary<string, string>? env = null)
    {
        try
        {
            // Both streams redirected: once attached, anything the helper (or the .NET runtime, on a crash) writes to an
            // unredirected stream would land on the user's Claude Code window.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "rc-nudge", pid.ToString(), sessionId }.Concat(manual ? ["--manual"] : [])) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("not started");
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync(); // drained so a chatty crash can't block the helper
            try { await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch (InvalidOperationException) { } return "error timeout"; }
            return (await read).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "error no-output";
        }
        catch (Exception e) { return "error " + e.GetType().Name; }
    }

    /// Another process's console, attached for the life of this object.
    sealed class Attached : IDisposable
    {
        readonly SafeFileHandle _in, _out;
        Attached(SafeFileHandle i, SafeFileHandle o) => (_in, _out) = (i, o);

        public static Attached? Open(int pid, out string why)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) { why = $"attach-failed {Marshal.GetLastWin32Error()}"; return null; }
            var i = CreateFileW("CONIN$", 0xC0000000, 3, 0, 3, 0, 0);
            var o = CreateFileW("CONOUT$", 0xC0000000, 3, 0, 3, 0, 0);
            if (!i.IsInvalid && !o.IsInvalid) { why = ""; return new(i, o); }
            i.Dispose(); o.Dispose(); FreeConsole();
            why = "console-handles-unavailable";
            return null;
        }

        public (List<string> Rows, List<ushort[]?> Attrs, int Width, int CursorRow)? Read()
        {
            if (!GetConsoleScreenBufferInfo(_out, out var info)) return null;
            var w = info.srWindow.Right - info.srWindow.Left + 1;
            if (w <= 0 || info.srWindow.Bottom < info.srWindow.Top) return null;
            var (rows, attrs) = (new List<string>(), new List<ushort[]?>());
            var (chars, cells) = (new char[w], new ushort[w]);
            for (var y = info.srWindow.Top; y <= info.srWindow.Bottom; y++)
            {
                var at = new COORD { X = info.srWindow.Left, Y = y };
                if (!ReadConsoleOutputCharacterW(_out, chars, (uint)w, at, out var n)) return null;
                rows.Add(new string(chars, 0, (int)n));
                attrs.Add(ReadConsoleOutputAttribute(_out, cells, (uint)w, at, out var m) && m == w ? (ushort[])cells.Clone() : null);
            }
            return (rows, attrs, w, info.dwCursorPosition.Y - info.srWindow.Top);
        }

        /// Every character as a key-down and key-up, in one write; the number of records written.
        public int Type(string text)
        {
            var records = text.SelectMany(c => KeyPair(c, VkKeyScanW(c) is var vk and >= 0 ? (ushort)(vk & 0xFF) : (ushort)0)).ToArray();
            return WriteConsoleInputW(_in, records, (uint)records.Length, out var n) ? (int)n : 0;
        }

        public bool Key(char c, ushort vk)
        {
            var records = KeyPair(c, vk);
            return WriteConsoleInputW(_in, records, 2, out var n) && n == 2;
        }

        public void Backspaces(int count)
        {
            var records = Enumerable.Repeat(KeyPair('\b', 0x08), count).SelectMany(x => x).ToArray();
            WriteConsoleInputW(_in, records, (uint)records.Length, out _);
        }

        public bool Pending() => GetNumberOfConsoleInputEvents(_in, out var n) && n > 0;

        static INPUT_RECORD[] KeyPair(char c, ushort vk)
        {
            var scan = (ushort)MapVirtualKeyW(vk, 0);
            INPUT_RECORD R(bool down) => new() { EventType = 1, Key = new KEY_EVENT_RECORD { bKeyDown = down ? 1 : 0, wRepeatCount = 1, wVirtualKeyCode = vk, wVirtualScanCode = scan, UnicodeChar = c } };
            return [R(true), R(false)];
        }

        public void Dispose()
        {
            _in.Dispose();
            _out.Dispose();
            FreeConsole();
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SMALL_RECT { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct CONSOLE_SCREEN_BUFFER_INFO { public COORD dwSize, dwCursorPosition; public ushort wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct KEY_EVENT_RECORD { public int bKeyDown; public ushort wRepeatCount, wVirtualKeyCode, wVirtualScanCode; public char UnicodeChar; public uint dwControlKeyState; }
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD Key; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleScreenBufferInfo(SafeFileHandle h, out CONSOLE_SCREEN_BUFFER_INFO info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ReadConsoleOutputCharacterW(SafeFileHandle h, [Out] char[] buf, uint length, COORD at, out uint read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadConsoleOutputAttribute(SafeFileHandle h, [Out] ushort[] buf, uint length, COORD at, out uint read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WriteConsoleInputW(SafeFileHandle h, INPUT_RECORD[] records, uint length, out uint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNumberOfConsoleInputEvents(SafeFileHandle h, out uint count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanW(char c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint MapVirtualKeyW(uint code, uint mapType);
}
