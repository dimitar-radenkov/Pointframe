using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SnippingTool.Services;

internal sealed class MouseHookService : IMouseHookService
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;

    private readonly ILogger<MouseHookService> _logger;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook;

    public MouseHookService(ILogger<MouseHookService> logger)
    {
        _logger = logger;
        _mouseProc = MouseHookCallback;
    }

    public event EventHandler<MouseHookEventArgs>? MouseButtonDown;

    public void Start()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var hMod = GetModuleHandle(process.MainModule?.ModuleName);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hMod, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _logger.LogWarning(
                "Failed to register low-level mouse hook (error {Code}); click ripple tracking will be disabled",
                Marshal.GetLastWin32Error());
        }
    }

    public void Stop()
    {
        if (_mouseHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && TryMapMouseButtonDownMessage(wParam, lParam, out var button, out var screenPoint))
        {
            MouseButtonDown?.Invoke(this, new MouseHookEventArgs(button, screenPoint));
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool TryMapMouseButtonDownMessage(
        IntPtr wParam,
        IntPtr lParam,
        out MouseHookButton button,
        out Point screenPoint)
    {
        var mouseInfo = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        screenPoint = new Point(mouseInfo.pt.x, mouseInfo.pt.y);

        switch (wParam.ToInt32())
        {
            case WmLButtonDown:
                button = MouseHookButton.Left;
                return true;
            case WmRButtonDown:
                button = MouseHookButton.Right;
                return true;
            case WmMButtonDown:
                button = MouseHookButton.Middle;
                return true;
            case WmXButtonDown:
                button = (HIWORD(mouseInfo.mouseData) & 0xFFFF) == XButton1
                    ? MouseHookButton.X1
                    : MouseHookButton.X2;
                return true;
            default:
                button = MouseHookButton.Left;
                screenPoint = default;
                return false;
        }
    }

    private static int HIWORD(uint number)
    {
        return (short)((number >> 16) & 0xFFFF);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable IDE1006 // P/Invoke struct fields must match Windows API names exactly
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
#pragma warning restore IDE1006
}
