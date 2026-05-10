using System.Runtime.InteropServices;

namespace Pointframe;

internal static class NativeMethods
{
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const uint VK_ESCAPE = 0x1B;
    internal const uint VK_PRINTSCREEN = 0x2C;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12; // Alt
    internal const uint VK_LSHIFT = 0xA0;
    internal const uint VK_RSHIFT = 0xA1;
    internal const uint VK_LCONTROL = 0xA2;
    internal const uint VK_RCONTROL = 0xA3;
    internal const uint VK_LMENU = 0xA4;
    internal const uint VK_RMENU = 0xA5;
    internal const uint VK_LWIN = 0x5B;
    internal const uint VK_RWIN = 0x5C;

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable IDE1006 // P/Invoke struct fields must match Windows API names exactly
    internal struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
#pragma warning restore IDE1006
}
