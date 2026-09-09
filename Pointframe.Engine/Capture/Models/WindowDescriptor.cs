namespace Pointframe.Engine;

public sealed record WindowDescriptor(
    long Hwnd,
    string Title,
    string ProcessName,
    int ProcessId,
    PixelBounds BoundsPixels,
    string? MonitorName,
    bool IsMinimized);
