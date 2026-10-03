using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Pointframe.Engine;

public interface IWindowContentCapture
{
    Bitmap? Capture(long hwnd, PixelBounds visibleFrame);
}

// Renders one window's own contents, not the screen at its position. A screen copy shows whatever lies on
// top of the window, which made evidence screenshots show other applications when the target was covered.
public sealed class WindowContentCapture : IWindowContentCapture
{
    // Asks DWM for the composed content, which DirectComposition, WPF and browser windows need; without it
    // they render black.
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public Bitmap? Capture(long hwnd, PixelBounds visibleFrame)
    {
        if (visibleFrame.Width <= 0 || visibleFrame.Height <= 0 || !GetWindowRect(new nint(hwnd), out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // PrintWindow draws the whole window rectangle, including the invisible resize border; render that,
        // then crop to the visible frame so the result matches the window as a user sees it.
        using var whole = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(whole))
        {
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(new nint(hwnd), hdc, PwRenderFullContent))
                {
                    return null;
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var crop = Rectangle.Intersect(
            new Rectangle(visibleFrame.X - rect.Left, visibleFrame.Y - rect.Top, visibleFrame.Width, visibleFrame.Height),
            new Rectangle(0, 0, width, height));
        return crop.Width <= 0 || crop.Height <= 0 ? null : whole.Clone(crop, PixelFormat.Format32bppRgb);
    }
}
