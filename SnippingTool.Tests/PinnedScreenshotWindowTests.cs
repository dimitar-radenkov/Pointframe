using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests;

public sealed class PinnedScreenshotWindowTests
{
    private const int WmNchittest = 0x0084;

    [Fact]
    public void Constructor_SizesWindowToBitmapAndCentersIt()
    {
        StaTestHelper.Run(() =>
        {
            var bitmap = CreateBitmap(10, 10);
            var window = new PinnedScreenshotWindow(bitmap);
            try
            {
                Assert.Equal(12, window.Width);
                Assert.Equal(20, window.Height);
                Assert.Equal((SystemParameters.PrimaryScreenWidth - window.Width) / 2, window.Left);
                Assert.Equal((SystemParameters.PrimaryScreenHeight - window.Height) / 2, window.Top);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WindowEnterAndLeave_ToggleCloseButtonVisibility()
    {
        StaTestHelper.Run(() =>
        {
            var bitmap = CreateBitmap(10, 10);
            var window = new PinnedScreenshotWindow(bitmap);
            try
            {
                window.Show();
                var closeButton = (System.Windows.Controls.Button)window.FindName("CloseBtn")!;

                InvokePrivate(window, "Window_MouseEnter", null, null);
                Assert.Equal(Visibility.Visible, closeButton.Visibility);

                InvokePrivate(window, "Window_MouseLeave", null, null);
                Assert.Equal(Visibility.Hidden, closeButton.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void WndProc_IgnoresNonHitTestMessages()
    {
        StaTestHelper.Run(() =>
        {
            var bitmap = CreateBitmap(100, 100);
            var window = new PinnedScreenshotWindow(bitmap);
            try
            {
                window.Show();
                var hwnd = new WindowInteropHelper(window).Handle;

                var result = InvokeWndProc(window, hwnd, 0, 0, out var handled, WmNchittest + 1);

                Assert.Equal(IntPtr.Zero, result);
                Assert.False(handled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static BitmapSource CreateBitmap(int width, int height)
    {
        var pixels = new int[width * height];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static IntPtr InvokeWndProc(
        PinnedScreenshotWindow window,
        IntPtr hwnd,
        int x,
        int y,
        out bool handled,
        int? messageOverride = null)
    {
        var method = typeof(PinnedScreenshotWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var lParam = new IntPtr((y << 16) | (x & 0xFFFF));
        var args = new object[]
        {
            hwnd,
            messageOverride ?? WmNchittest,
            IntPtr.Zero,
            lParam,
            false,
        };

        var result = (IntPtr)method.Invoke(window, args)!;
        handled = (bool)args[4];
        return result;
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, args);
    }
}
