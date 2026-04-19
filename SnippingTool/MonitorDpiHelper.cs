using System.Runtime.InteropServices;
using System.Windows;

namespace Pointframe;

internal static class MonitorDpiHelper
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

    internal static double GetMonitorScale(System.Drawing.Point screenPoint)
    {
        var monitor = MonitorFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y }, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return 1d;
        }

        var result = GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _);
        if (result != 0 || dpiX == 0)
        {
            return 1d;
        }

        return dpiX / 96d;
    }

    internal static Rect CalculateWindowBounds(System.Drawing.Rectangle screenBoundsPixels, double monitorScale)
    {
        return new Rect(
            screenBoundsPixels.Left / monitorScale,
            screenBoundsPixels.Top / monitorScale,
            screenBoundsPixels.Width / monitorScale,
            screenBoundsPixels.Height / monitorScale);
    }
}
