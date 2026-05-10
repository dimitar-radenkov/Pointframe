using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Pointframe;

public partial class PinnedScreenshotWindow : Window
{
    // ── WM_NCHITTEST constants ─────────────────────────────────────────────
    private const int WmNchittest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    // HtTop (12) intentionally skipped: the drag strip covers the top edge,
    // so top-edge resize is replaced by DragMove() on the strip.
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    /// <summary>Width in physical pixels of each resize hit zone.</summary>
    private const int HitZone = 8;

    // ──────────────────────────────────────────────────────────────────────

    public PinnedScreenshotWindow(BitmapSource bitmap)
    {
        InitializeComponent();
        ScreenshotImage.Source = bitmap;

        // Size window to the bitmap's pixel dimensions, capped at 80 % of screen.
        var maxW = SystemParameters.PrimaryScreenWidth * 0.8;
        var maxH = SystemParameters.PrimaryScreenHeight * 0.8;
        var scale = Math.Min(1.0, Math.Min(maxW / bitmap.PixelWidth,
                                              maxH / bitmap.PixelHeight));

        Width = bitmap.PixelWidth * scale + 2;   // +2 for 1 px border each side
        Height = bitmap.PixelHeight * scale + 8 + 2; // +8 drag strip, +2 border

        // Centre on the primary screen.
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
    }

    // ── Win32 resize hit-testing ──────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                           ref bool handled)
    {
        if (msg != WmNchittest)
        {
            return IntPtr.Zero;
        }

        // lParam contains cursor screen coordinates packed as signed 16-bit ints.
        int screenX = (short)(lParam.ToInt64() & 0xFFFF);
        int screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        // Use Win32 GetWindowRect for physical-pixel accuracy (DPI-safe).
        GetWindowRect(hwnd, out var rc);
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        var relX = screenX - rc.Left;
        var relY = screenY - rc.Top;

        var onLeft = relX < HitZone;
        var onRight = relX >= w - HitZone;
        var onTop = relY < HitZone;
        var onBottom = relY >= h - HitZone;

        // Evaluate corners first (highest priority).
        if (onTop && onLeft)
        {
            handled = true;
            return new IntPtr(HtTopLeft);
        }

        if (onTop && onRight)
        {
            handled = true;
            return new IntPtr(HtTopRight);
        }

        if (onBottom && onLeft)
        {
            handled = true;
            return new IntPtr(HtBottomLeft);
        }

        if (onBottom && onRight)
        {
            handled = true;
            return new IntPtr(HtBottomRight);
        }

        // Then individual edges.
        if (onLeft)
        {
            handled = true;
            return new IntPtr(HtLeft);
        }

        if (onRight)
        {
            handled = true;
            return new IntPtr(HtRight);
        }

        if (onBottom)
        {
            handled = true;
            return new IntPtr(HtBottom);
        }

        // Top edge is NOT returned as HtTop — the drag strip covers that area and
        // MouseLeftButtonDown on the client area handles DragMove() instead.

        return IntPtr.Zero;
    }

    // ── Drag (whole window, except the close button) ──────────────────────

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Let the close button handle its own click; drag everything else.
        if (e.OriginalSource is not System.Windows.Controls.Button)
        {
            DragMove();
        }
    }

    // ── Keyboard ─────────────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    // ── Close button visibility on hover ─────────────────────────────────

    private void Window_MouseEnter(object sender, MouseEventArgs e) =>
        CloseBtn.Visibility = Visibility.Visible;

    private void Window_MouseLeave(object sender, MouseEventArgs e) =>
        CloseBtn.Visibility = Visibility.Hidden;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) =>
        Close();
}
