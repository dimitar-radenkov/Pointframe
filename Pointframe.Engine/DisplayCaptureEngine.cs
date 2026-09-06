using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Pointframe.Engine;

public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    public Rectangle ToRectangle()
    {
        return new Rectangle(X, Y, Width, Height);
    }
}

public sealed record DisplayDescriptor(
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds BoundsPixels,
    PixelBounds WorkAreaBoundsPixels = default);

public sealed class CapturedMonitor(DisplayDescriptor Display, Bitmap Bitmap) : IDisposable
{
    public DisplayDescriptor Display { get; } = Display;

    public Bitmap Bitmap { get; } = Bitmap;

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}

public interface IDisplayCaptureEngine
{
    IReadOnlyList<DisplayDescriptor> GetDisplays();

    Bitmap Capture(PixelBounds boundsPixels);

    CapturedMonitor CaptureMonitor(string monitorName);
}

public sealed class DisplayCaptureEngine : IDisplayCaptureEngine
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MonitorDpiTypeEffective = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        return Screen.AllScreens
            .Select(CreateDescriptor)
            .ToArray();
    }

    public Bitmap Capture(PixelBounds boundsPixels)
    {
        if (boundsPixels.Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundsPixels), "The capture width must be positive.");
        }

        if (boundsPixels.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boundsPixels), "The capture height must be positive.");
        }

        var bitmap = new Bitmap(boundsPixels.Width, boundsPixels.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                boundsPixels.X,
                boundsPixels.Y,
                0,
                0,
                new Size(boundsPixels.Width, boundsPixels.Height),
                CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public CapturedMonitor CaptureMonitor(string monitorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorName);

        var display = GetDisplays().SingleOrDefault(display =>
            string.Equals(display.MonitorName, monitorName, StringComparison.OrdinalIgnoreCase));
        if (display is null)
        {
            throw new ArgumentException($"The monitor '{monitorName}' was not found.", nameof(monitorName));
        }

        return new CapturedMonitor(display, Capture(display.BoundsPixels));
    }

    private static DisplayDescriptor CreateDescriptor(Screen screen)
    {
        var bounds = screen.Bounds;
        var (dpiScaleX, dpiScaleY) = GetDpiScale(bounds.Location);
        return new DisplayDescriptor(
            screen.DeviceName,
            dpiScaleX,
            dpiScaleY,
            new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new PixelBounds(
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height));
    }

    private static (double DpiScaleX, double DpiScaleY) GetDpiScale(Point screenPoint)
    {
        var monitor = MonitorFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y }, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return (1d, 1d);
        }

        var result = GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out var dpiY);
        if (result != 0 || dpiX == 0 || dpiY == 0)
        {
            return (1d, 1d);
        }

        return (dpiX / 96d, dpiY / 96d);
    }
}