using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

// No real browser is touched: install roots are temp folders, windows are this process's own forms.
[Collection("process environment")] // points LOCALAPPDATA and ProgramFiles at temp folders
public class WindowsBrowserHostTests
{
    [Fact]
    public void ExeIsFoundUnderLocalAppDataFirstAndChromeAndCanaryStayApart()
    {
        using var local = new TempDir();
        using var programs = new TempDir();
        string Put(string root, string rel) { var f = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, ""); return f; }
        var saved = new[] { "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", local.Path);
            Environment.SetEnvironmentVariable("ProgramFiles", programs.Path);
            Environment.SetEnvironmentVariable("ProgramFiles(x86)", null);
            var chromeSystem = Put(programs.Path, @"Google\Chrome\Application\chrome.exe");
            var canary = Put(local.Path, @"Google\Chrome SxS\Application\chrome.exe");
            Assert.Equal(chromeSystem, BrowserProfiles.Find("chrome")!.ExePath());
            Assert.Equal(canary, BrowserProfiles.Find("chrome-canary")!.ExePath());
            var chromeUser = Put(local.Path, @"Google\Chrome\Application\chrome.exe");
            Assert.Equal(chromeUser, BrowserProfiles.Find("chrome")!.ExePath());
            Assert.Null(WindowsBrowserHost.For("edge")); // not installed
            Assert.Equal(["chrome", "chrome-canary"], BrowserProfiles.Installed().Select(b => b.Id));
            Assert.False(WindowsBrowserHost.For("chrome")!.Running()); // a file nobody runs
            Assert.Equal(0, WindowsBrowserHost.For("chrome")!.CloseWindows("Personal"));
        }
        finally { foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v); }
    }

    [Fact]
    public void RunningComparesTheFullImagePath()
    {
        Assert.True(new WindowsBrowserHost(Environment.ProcessPath!).Running());
        Assert.Contains(Environment.ProcessId, new WindowsBrowserHost(Environment.ProcessPath!.ToUpperInvariant()).Pids());
        // Same exe name, another folder: not this process.
        Assert.False(new WindowsBrowserHost(Path.Combine(Path.GetTempPath(), Path.GetFileName(Environment.ProcessPath!))).Running());
    }

    [Fact]
    public void ClosesOnlyTheWindowsWhoseButtonNamesTheProfile()
    {
        var name = "profile-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
        Form? hit = null, miss = null, page = null;
        var ready = new ManualResetEventSlim();
        // Their own UI thread: UI Automation must not be called from the thread that owns the windows.
        var ui = new Thread(() =>
        {
            Form Make(string button)
            {
                var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
                f.Controls.Add(new Button { Text = button });
                f.Show();
                return f;
            }
            hit = Make(name);
            miss = Make("someone-else@example.com");
            // The new account's window: its avatar names someone else, a page in it names the old profile.
            page = Make("someone-else@example.com");
            var doc = new Panel { AccessibleRole = AccessibleRole.Document, Dock = DockStyle.Fill };
            doc.Controls.Add(new Button { Text = name });
            page.Controls.Add(doc);
            ready.Set();
            Application.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            var cls = (string)hit!.Invoke(() => WindowsBrowserHost.ClassOf(hit.Handle));
            Assert.Equal(1, WindowsBrowserHost.CloseWindows(new HashSet<int> { Environment.ProcessId }, cls, name));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!hit.IsDisposed && DateTime.UtcNow < until) Thread.Sleep(50);
            Assert.True(hit.IsDisposed, "WM_CLOSE closed the matching window");
            Assert.False(miss!.IsDisposed);
            Assert.False(page!.IsDisposed, "a button inside a page never counts");
            Assert.Equal(0, WindowsBrowserHost.CloseWindows(new HashSet<int> { Environment.ProcessId + 1 }, cls, "someone-else@example.com")); // another process's pid
        }
        finally
        {
            if (miss is { IsDisposed: false }) miss.Invoke(Application.ExitThread);
            ui.Join(TimeSpan.FromSeconds(10));
        }
    }
}
