using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pointframe.Engine;

public sealed class WindowDiscoveryService : IWindowDiscoveryService
{
    private const uint MonitorDefaultToNearest = 2;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, char[] buffer, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private readonly int _currentProcessId;

    public WindowDiscoveryService()
    {
        using var currentProcess = Process.GetCurrentProcess();
        _currentProcessId = currentProcess.Id;
    }

    internal WindowDiscoveryService(int currentProcessId)
    {
        _currentProcessId = currentProcessId;
    }

    public IReadOnlyList<WindowDescriptor> GetWindows()
    {
        var windows = new List<WindowDescriptor>();

        EnumWindows((hwnd, _) =>
        {
            if (TryCreateDescriptor(hwnd, out var descriptor))
            {
                windows.Add(descriptor);
            }

            return true;
        }, nint.Zero);

        return windows;
    }

    public WindowDescriptor? GetWindow(long hwnd)
    {
        var handle = (nint)hwnd;
        return TryCreateDescriptor(handle, out var descriptor) ? descriptor : null;
    }

    private bool TryCreateDescriptor(nint hwnd, out WindowDescriptor descriptor)
    {
        descriptor = default!;

        if (!IsWindowVisible(hwnd))
        {
            return false;
        }

        var titleLength = GetWindowTextLength(hwnd);
        if (titleLength <= 0)
        {
            return false;
        }

        var buffer = new char[titleLength + 1];
        GetWindowText(hwnd, buffer, buffer.Length);
        var title = new string(buffer, 0, titleLength);

        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var isMinimized = IsIconic(hwnd);

        GetWindowThreadProcessId(hwnd, out var processId);
        if ((int)processId == _currentProcessId)
        {
            return false;
        }

        string processName;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process exited between enumeration and lookup.
            return false;
        }

        var monitorName = ResolveMonitorName(ref rect);

        descriptor = new WindowDescriptor(
            hwnd.ToInt64(),
            title,
            processName,
            (int)processId,
            new PixelBounds(rect.Left, rect.Top, width, height),
            monitorName,
            isMinimized);

        return true;
    }

    private static string? ResolveMonitorName(ref RECT rect)
    {
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return null;
        }

        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(monitor, ref info) ? info.DeviceName : null;
    }
}
