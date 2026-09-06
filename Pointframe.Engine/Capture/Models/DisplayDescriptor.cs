namespace Pointframe.Engine;

public sealed record DisplayDescriptor(
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds BoundsPixels,
    PixelBounds WorkAreaBoundsPixels = default);
