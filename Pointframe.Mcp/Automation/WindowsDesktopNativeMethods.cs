using System.Runtime.InteropServices;

namespace Pointframe.Mcp.Automation;

internal static class WindowsDesktopNativeMethods
{
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const uint MouseEventAbsolute = 0x8000;
    internal const uint MouseEventMove = 0x0001;
    internal const uint MouseEventLeftDown = 0x0002;
    internal const uint MouseEventLeftUp = 0x0004;
    internal const uint MouseEventRightDown = 0x0008;
    internal const uint MouseEventRightUp = 0x0010;
    internal const uint MouseEventWheel = 0x0800;
    internal const uint MouseEventXDown = 0x0080;
    internal const uint MouseEventXUp = 0x0100;
    internal const uint KeyEventUnicode = 0x0004;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const ushort VirtualKeyPause = 0x13;
    internal const ushort VirtualKeyControl = 0x11;
    internal const ushort VirtualKeyAlt = 0x12;
    internal const int VirtualScreenX = 76;
    internal const int VirtualScreenY = 77;
    internal const int VirtualScreenWidth = 78;
    internal const int VirtualScreenHeight = 79;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(ushort virtualKey);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MOUSEINPUT Mouse;

        [FieldOffset(0)]
        internal KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
        internal uint DwFlags;
        internal uint Time;
        internal nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort Vk;
        internal ushort Scan;
        internal uint DwFlags;
        internal uint Time;
        internal nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
