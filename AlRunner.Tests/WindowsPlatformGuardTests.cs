// WindowsPlatformGuardTests — locks the fast, loud failure that replaces the raw
// DllNotFoundException Windows hit before this fix (#1650): JmpHook's
// InstallIndirect calls the Unix mprotect P/Invoke unconditionally, so a Windows
// run used to die hundreds of frames into patch install with no actionable
// message. BcRuntime.ThrowIfUnsupportedPlatform is the choke point that now
// fails first, with a message naming the issue and the real cause.

using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class WindowsPlatformGuardTests
{
    [Fact]
    public void ThrowIfUnsupportedPlatform_OnWindows_ThrowsPlatformNotSupported()
    {
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => BcRuntime.ThrowIfUnsupportedPlatform(isWindows: true));

        Assert.Contains("mprotect", ex.Message);
        Assert.Contains("1650", ex.Message);
    }

    [Fact]
    public void ThrowIfUnsupportedPlatform_OnNonWindows_DoesNotThrow()
    {
        // Negative: the guard must not fire for the platforms al-runner actually
        // supports (Linux/macOS both report isWindows=false here).
        BcRuntime.ThrowIfUnsupportedPlatform(isWindows: false);
    }
}
