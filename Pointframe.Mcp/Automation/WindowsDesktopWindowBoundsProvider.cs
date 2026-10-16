using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

public interface IDesktopWindowBoundsProvider
{
    // Physical desktop pixels, the unit capture and click coordinates use, so a window on a monitor with a
    // different scale needs no conversion.
    IReadOnlyList<PixelBounds> GetVisibleWindowBounds(int processId);
}

internal static class DesktopWindowBounds
{
    public static IReadOnlyList<PixelBounds> Normalize(
        IEnumerable<PixelBounds> windows,
        PixelBounds virtualScreen,
        int maxCount)
    {
        var result = new List<PixelBounds>();
        foreach (var window in windows)
        {
            var left = Math.Max(window.X, virtualScreen.X);
            var top = Math.Max(window.Y, virtualScreen.Y);
            var right = Math.Min(window.X + window.Width, virtualScreen.X + virtualScreen.Width);
            var bottom = Math.Min(window.Y + window.Height, virtualScreen.Y + virtualScreen.Height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            var clipped = new PixelBounds(left, top, right - left, bottom - top);
            if (!result.Contains(clipped))
            {
                result.Add(clipped);
            }

            if (result.Count >= maxCount)
            {
                break;
            }
        }

        return result;
    }
}

internal sealed class WindowsDesktopWindowBoundsProvider : IDesktopWindowBoundsProvider
{
    public IReadOnlyList<PixelBounds> GetVisibleWindowBounds(int processId)
    {
        var windows = new List<PixelBounds>();
        WindowsDesktopNativeMethods.EnumWindows(
            (handle, _) =>
            {
                if (WindowsDesktopNativeMethods.GetWindowThreadProcessId(handle, out var owner) != 0
                    && owner == (uint)processId
                    && WindowsDesktopNativeMethods.IsWindowVisible(handle)
                    && !WindowsDesktopNativeMethods.IsIconic(handle)
                    && WindowsDesktopNativeMethods.GetWindowRect(handle, out var rect))
                {
                    windows.Add(new PixelBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                }

                return true;
            },
            0);

        var virtualScreen = new PixelBounds(
            WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenX),
            WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenY),
            WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenWidth),
            WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenHeight));
        return DesktopWindowBounds.Normalize(windows, virtualScreen, DesktopTestingLimits.MaxObservationCount);
    }
}
