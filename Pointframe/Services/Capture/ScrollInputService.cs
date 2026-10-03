namespace Pointframe.Services;

// Scrolls the window under a screen point with synthetic mouse-wheel input. The cursor is parked on the
// point for the whole capture because Windows routes wheel input to the window under the cursor when the
// input is processed, not when it is sent. Coordinates are physical pixels, so every call runs PerMonitorV2.
internal sealed class ScrollInputService : IScrollInputService
{
    private const uint InputMouse = 0;
    private const uint MouseEventWheel = 0x0800;
    private const int WheelDelta = 120;

    private readonly ILogger<ScrollInputService> _logger;

    public ScrollInputService(ILogger<ScrollInputService> logger)
    {
        _logger = logger;
    }

    public IDisposable BeginScrolling(int screenX, int screenY)
    {
        var original = default(NativePoint);
        DpiAwarenessScope.RunPerMonitorV2(() =>
        {
            GetCursorPos(out original);
            SetCursorPos(screenX, screenY);
        });
        _logger.LogDebug("Scrolling capture parked the cursor at ({X},{Y})", screenX, screenY);
        return new CursorRestore(original);
    }

    public void ScrollDown(int wheelNotches)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wheelNotches);
        var inputs = new Input[wheelNotches];
        for (var i = 0; i < wheelNotches; i++)
        {
            inputs[i] = new Input
            {
                Type = InputMouse,
                Mouse = new MouseInput { MouseData = unchecked((uint)-WheelDelta), Flags = MouseEventWheel },
            };
        }

        var sent = 0u;
        DpiAwarenessScope.RunPerMonitorV2(() => sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()));
        if (sent != inputs.Length)
        {
            _logger.LogWarning("Scrolling capture sent {Sent} of {Requested} wheel inputs", sent, inputs.Length);
        }
    }

    private sealed class CursorRestore(NativePoint original) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DpiAwarenessScope.RunPerMonitorV2(() => SetCursorPos(original.X, original.Y));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
