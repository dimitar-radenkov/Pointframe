using System.Runtime.InteropServices;
using System.Windows;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.Views;

public sealed class ScrollingCaptureProgressWindowTests
{
    [Theory]
    [InlineData(0, 0, 100, 100, 50, 50, 20, 20, true)]
    [InlineData(0, 0, 100, 100, 100, 10, 20, 20, false)]
    [InlineData(0, 0, 100, 100, 99, 99, 20, 20, true)]
    [InlineData(0, 0, 100, 100, 110, 110, 20, 20, false)]
    public void ShouldHideForCapture_UsesPhysicalPixelRectangleIntersection(
        int windowX,
        int windowY,
        int windowWidth,
        int windowHeight,
        int regionX,
        int regionY,
        int regionWidth,
        int regionHeight,
        bool expected)
    {
        Assert.Equal(
            expected,
            ScrollingCaptureProgressWindow.ShouldHideForCapture(
                new Int32Rect(windowX, windowY, windowWidth, windowHeight),
                new Int32Rect(regionX, regionY, regionWidth, regionHeight)));
    }

    [Fact]
    public void Show_DoesNotActivateAndAddsNoActivateExtendedStyle()
    {
        StaTestHelper.Run(() =>
        {
            var window = new ScrollingCaptureProgressWindow(
                new ScrollingCaptureProgressViewModel(new CancellationTokenSource()),
                new Int32Rect(100, 100, 400, 300));
            try
            {
                window.Show();
                var style = GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(window).Handle, -20).ToInt64();
                Assert.False(window.IsActive);
                Assert.NotEqual(0, style & 0x08000000);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
}
