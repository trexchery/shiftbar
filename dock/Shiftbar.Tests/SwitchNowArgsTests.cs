using Xunit;

namespace Shiftbar.Tests;

/// The tray's and the macOS backend's unattended switch share one call. Where nothing follows the two logins of a held account
/// (macOS) it stays `--exclusive`; elsewhere (Windows) it is not, and the engine's own follow keeps the logins together.
public class SwitchNowArgsTests
{
    [Fact]
    public void MacOSKeepsTheExclusiveRefusalOfAHeldAccount() =>
        Assert.Equal(["switch", "3", "--json", "--auto", "--exclusive"], Engine.SwitchNowArgs(3, macOS: true));

    [Fact]
    public void WindowsSwitchesOntoAHeldAccount() =>
        Assert.Equal(["switch", "3", "--json", "--auto"], Engine.SwitchNowArgs(3, macOS: false));

    /// The automatic check never runs consume-first, whatever the saved strategy: it moved a login that still had room.
    [Fact]
    public void AutoCheckIsAlwaysBest() =>
        Assert.Equal(["auto", "--once", "--json", "--strategy", "best", "--threshold", "92.5"], Engine.AutoOnceArgs(92.5));
}
