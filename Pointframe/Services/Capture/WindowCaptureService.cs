using System.Windows;
using System.Windows.Interop;

namespace Pointframe.Services;

internal sealed class WindowCaptureService : IWindowCaptureService
{
    private const uint PwRenderFullContent = 0x00000002;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly ILogger<WindowCaptureService> _logger;

    public WindowCaptureService(
        IScreenCaptureService screenCaptureService,
        ILogger<WindowCaptureService> logger)
    {
        _screenCaptureService = screenCaptureService;
        _logger = logger;
    }

    public bool TryCaptureWindowUnderCursor(out BitmapSource? bitmap)
    {
        bitmap = null;

        if (!GetCursorPos(out var cursor))
        {
            _logger.LogWarning("GetCursorPos failed while trying to capture window under cursor.");
            return false;
        }

        var hitWindow = WindowFromPoint(cursor);
        if (hitWindow == IntPtr.Zero)
        {
            _logger.LogWarning("WindowFromPoint returned no window at {X},{Y}.", cursor.X, cursor.Y);
            return false;
        }

        var rootWindow = GetAncestor(hitWindow, GetRootAncestorFlag);
        if (rootWindow == IntPtr.Zero)
        {
            rootWindow = hitWindow;
        }

        if (!IsWindowVisible(rootWindow))
        {
            _logger.LogInformation("Window under cursor is not visible.");
            return false;
        }

        if (!GetWindowRect(rootWindow, out var rect))
        {
            _logger.LogWarning("GetWindowRect failed for window under cursor.");
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 1 || height <= 1)
        {
            _logger.LogInformation("Window under cursor has invalid bounds {Width}x{Height}.", width, height);
            return false;
        }

        using var windowBitmap = new System.Drawing.Bitmap(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var graphics = System.Drawing.Graphics.FromImage(windowBitmap);
        var hdc = graphics.GetHdc();
        var printed = false;
        try
        {
            printed = PrintWindow(rootWindow, hdc, PwRenderFullContent);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        if (printed)
        {
            var hBitmap = windowBitmap.GetHbitmap();
            try
            {
                bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return true;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        _logger.LogDebug(
            "PrintWindow failed for hwnd {Handle}. Falling back to screen capture at {X},{Y} {W}x{H}.",
            rootWindow,
            rect.Left,
            rect.Top,
            width,
            height);

        try
        {
            bitmap = _screenCaptureService.Capture(rect.Left, rect.Top, width, height);
            bitmap.Freeze();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback screen capture failed for window under cursor.");
            return false;
        }
    }

    private const uint GetRootAncestorFlag = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
