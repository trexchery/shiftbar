using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Shiftbar;

/// An installed Chromium browser on Windows. Chrome and Canary share chrome.exe, so their processes are told apart by full image path.
sealed class WindowsBrowserHost(string exe) : IBrowserHost
{
    const string WindowClass = "Chrome_WidgetWin_1";

    public static IBrowserHost? For(string browserId) => BrowserProfiles.Find(browserId)?.ExePath() is { } exe ? new WindowsBrowserHost(exe) : null;

    internal HashSet<int> Pids()
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
                if (string.Equals(ImagePath(p.Id), exe, StringComparison.OrdinalIgnoreCase)) pids.Add(p.Id);
        return pids;
    }

    public bool Running() => Pids().Count > 0;

    /// A running browser hands the new process's request to itself and the new process exits.
    public void Launch(string profileDir)
    {
        using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { "--profile-directory=" + profileDir } });
    }

    public int CloseWindows(string profileName) => Pids() is { Count: > 0 } pids ? CloseWindows(pids, WindowClass, profileName) : 0;

    /// Each visible top-level window of `pids` and `windowClass` whose avatar button reads `profileName` is asked to close (WM_CLOSE,
    /// as its own X button does). Never call it on the thread that owns those windows: UI Automation would wait on itself.
    /// Only a button in the browser's own UI counts: pages sit under a Document element, and one of them (an account chooser,
    /// "Continue as <email>") may name the old profile inside the new account's window.
    internal static int CloseWindows(IReadOnlySet<int> pids, string windowClass, string profileName)
    {
        var windows = new List<nint>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out var pid) != 0 && pids.Contains((int)pid) && ClassOf(h) == windowClass) windows.Add(h);
            return true;
        }, 0);
        if (windows.Count == 0) return 0;
        var uia = (IUIAutomation)new CUIAutomation();
        var button = uia.CreateAndCondition(uia.CreatePropertyCondition(ControlTypeProperty, ButtonControlType), uia.CreatePropertyCondition(NameProperty, profileName));
        var walker = uia.ControlViewWalker();
        var closed = 0;
        foreach (var h in windows)
        {
            // ponytail: FindAll walks the pages too (and wakes their accessibility) before the Document filter; fine once per switch,
            // prune Document subtrees by hand if it ever shows up slow.
            try
            {
                var hits = uia.ElementFromHandle(h).FindAll(TreeScopeDescendants, button);
                if (!Enumerable.Range(0, hits.Length()).Any(i => !InPage(walker, hits.GetElement(i)))) continue;
            }
            catch (COMException) { continue; } // closed meanwhile
            if (PostMessage(h, WM_CLOSE, 0, 0)) closed++;
        }
        return closed;
    }

    static bool InPage(IUIAutomationTreeWalker walker, IUIAutomationElement e)
    {
        for (var p = walker.GetParentElement(e); p is not null; p = walker.GetParentElement(p))
            if (p.GetCurrentPropertyValue(ControlTypeProperty) is int type && type == DocumentControlType) return true;
        return false;
    }

    internal static string ClassOf(nint h)
    {
        var name = new StringBuilder(64);
        return GetClassName(h, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = path.Capacity;
            return QueryFullProcessImageName(h, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    const int WM_CLOSE = 0x0010, TreeScopeDescendants = 4, NameProperty = 30005, ControlTypeProperty = 30003, ButtonControlType = 50000, DocumentControlType = 50030;

    delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    // The few UI Automation calls used, declared in vtable order (UIAutomationClient.h); the skipped slots are never called.
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] class CUIAutomation;

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationCondition;

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElementArray
    {
        int Length();
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTreeWalker
    {
        IUIAutomationElement? GetParentElement(IUIAutomationElement element);
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(nint hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        IUIAutomationTreeWalker ControlViewWalker();
        void ContentViewWalker();
        void RawViewWalker();
        void RawViewCondition();
        void ControlViewCondition();
        void ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
        void CreatePropertyConditionEx();
        IUIAutomationCondition CreateAndCondition(IUIAutomationCondition a, IUIAutomationCondition b);
    }
}

/// The tray's side: every listing and every switch is observed, the pending closes are ticked from the watch poll.
/// One queue runs both off the UI thread, in order; a tick waits for the queue to be empty, so they never pile up.
public sealed partial class TrayApp
{
    BrowserFollow? _follow; // built and used only on the queue
    Task _browserQueue = Task.CompletedTask;
    DateTime _browserNext;

    string? ActiveEmail => _snap is { } s ? s.Accounts.FirstOrDefault(a => a.Number == s.ActiveAccountNumber)?.Email : null;

    void BrowserObserve()
    {
        var (email, number) = (ActiveEmail, _snap?.ActiveAccountNumber);
        BrowserQueue(b =>
        {
            // sequence.json is written on every switch: its time is the switch's when it names the same account.
            var login = ResumeStopped.DefaultLogin();
            b.Observe(email, number is not null && login.Account == number ? login.ChangedAt : null);
        });
    }

    void BrowserTick()
    {
        if (!_browserQueue.IsCompleted || DateTime.UtcNow < _browserNext) return;
        _browserNext = DateTime.UtcNow.AddSeconds(10);
        var email = ActiveEmail;
        BrowserQueue(b => b.Tick(email));
    }

    void BrowserQueue(Action<BrowserFollow> step) =>
        _browserQueue = _browserQueue.ContinueWith(_ => step(_follow ??= new(() => _settings, WindowsBrowserHost.For,
            () => ResumeStopped.LiveSessions([(ResumeStopped.DefaultDir, null)]), () => DateTime.UtcNow,
            Path.Combine(DockSettings.Dir, "browser_pending.json"), Program.Log)), TaskScheduler.Default);

    /// The account menu's "Browser profile" choices: Auto, then each profile of the chosen browser; null hides the submenu.
    IReadOnlyList<(string Label, string Action, bool Checked)>? BrowserChoices(Account a)
    {
        if (!_settings.BrowserFollowsAccount) return null;
        var profiles = BrowserProfiles.ReadState(_settings.Browser).Profiles;
        if (profiles.Count == 0) return null;
        var auto = BrowserProfiles.Resolve(a.Email, null, profiles);
        var picked = _settings.BrowserProfiles.TryGetValue(a.Email, out var dir)
            ? profiles.FirstOrDefault(p => string.Equals(p.Dir, dir, StringComparison.OrdinalIgnoreCase)) : null;
        return [($"Auto: match email{(auto != null ? $" ({auto.Name})" : "")}", "browser-profile:", picked == null),
            .. profiles.Select(p => (p.UserName.Length > 0 && p.UserName != p.Name ? $"{p.Name} · {p.UserName}" : p.Name, "browser-profile:" + p.Dir, p == picked))];
    }

    void SetBrowserProfile(Account a, string dir)
    {
        // A new dictionary, never edited in place: the queue reads it off the UI thread.
        var picks = new Dictionary<string, string>(_settings.BrowserProfiles, StringComparer.OrdinalIgnoreCase);
        if (dir.Length == 0) picks.Remove(a.Email);
        else picks[a.Email] = dir;
        _settings.BrowserProfiles = picks;
        _settings.Save();
    }
}
