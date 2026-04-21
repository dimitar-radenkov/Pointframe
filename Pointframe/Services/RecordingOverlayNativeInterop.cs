using System.Runtime.InteropServices;
using System.Windows;

namespace Pointframe.Services;

internal static class RecordingOverlayNativeInterop
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const uint InputMouse = 0;
    private const uint MouseEventfLeftDown = 0x0002;
    private const uint MouseEventfLeftUp = 0x0004;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT MouseInput;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static void MoveWindow(IntPtr handle, Int32Rect bounds)
    {
        _ = MoveWindow(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
    }

    public static Int32Rect? TryGetWindowRect(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect))
        {
            return null;
        }

        return new Int32Rect(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    public static void SetCursorScreenPosition(Point screenPoint)
    {
        _ = SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
    }

    public static Point? GetCursorScreenPoint()
    {
        if (GetCursorPos(out var nativePoint))
        {
            return new Point(nativePoint.X, nativePoint.Y);
        }

        return null;
    }

    public static void SendLeftClick()
    {
        INPUT[] inputs =
        [
            new()
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    MouseInput = new MOUSEINPUT
                    {
                        DwFlags = MouseEventfLeftDown,
                    },
                },
            },
            new()
            {
                Type = InputMouse,
                Union = new InputUnion
                {
                    MouseInput = new MOUSEINPUT
                    {
                        DwFlags = MouseEventfLeftUp,
                    },
                },
            },
        ];

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SetMouseTransparency(IntPtr handle, bool isTransparent)
    {
        var currentStyle = GetWindowExStyle(handle);
        var nextStyle = isTransparent
            ? currentStyle | WsExTransparent
            : currentStyle & ~WsExTransparent;

        if (nextStyle == currentStyle)
        {
            return;
        }

        SetWindowExStyle(handle, nextStyle);
        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static int GetWindowExStyle(IntPtr handle)
    {
        return IntPtr.Size == 8
            ? unchecked((int)GetWindowLongPtr64(handle, GwlExStyle).ToInt64())
            : GetWindowLong32(handle, GwlExStyle);
    }

    private static void SetWindowExStyle(IntPtr handle, int style)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(style));
            return;
        }

        _ = SetWindowLong32(handle, GwlExStyle, style);
    }
}
