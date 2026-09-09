using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class WindowDiscoveryServiceTests
{
    [Fact]
    public void GetWindows_ReturnsOnlyVisibleTopLevelWindowsWithNonEmptyTitles()
    {
        // WindowDiscoveryService calls real Win32 APIs, so this is a live-system smoke test.
        // It verifies basic invariants without requiring specific windows to exist.
        var sut = new WindowDiscoveryService();

        var windows = sut.GetWindows();

        // At minimum, the test runner's own console/IDE window should be visible.
        Assert.NotNull(windows);
        foreach (var window in windows)
        {
            Assert.False(string.IsNullOrEmpty(window.Title), "Window title must not be empty.");
            Assert.True(window.Hwnd > 0, "Window handle must be positive.");
            Assert.True(window.ProcessId > 0, "Process ID must be positive.");
            Assert.False(string.IsNullOrEmpty(window.ProcessName), "Process name must not be empty.");
            Assert.True(window.BoundsPixels.Width > 0, "Window width must be positive.");
            Assert.True(window.BoundsPixels.Height > 0, "Window height must be positive.");
        }
    }

    [Fact]
    public void GetWindows_ExcludesCurrentProcessWindows()
    {
        var sut = new WindowDiscoveryService();

        var windows = sut.GetWindows();
        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        Assert.DoesNotContain(windows, w => w.ProcessId == currentProcessId);
    }

    [Fact]
    public void GetWindow_WithInvalidHandle_ReturnsNull()
    {
        var sut = new WindowDiscoveryService();

        var result = sut.GetWindow(999_999_999);

        Assert.Null(result);
    }

    [Fact]
    public void GetWindow_WithZeroHandle_ReturnsNull()
    {
        var sut = new WindowDiscoveryService();

        var result = sut.GetWindow(0);

        Assert.Null(result);
    }
}
