using System.Windows;
using System.Windows.Media.Imaging;

namespace SnippingTool;

internal sealed record SelectionSessionResult(
    string MonitorName,
    BitmapSource MonitorSnapshot,
    BitmapSource SelectionBackground,
    Rect HostBoundsDips,
    Int32Rect HostBoundsPixels,
    Rect SelectionRectDips,
    Int32Rect SelectionBoundsPixels,
    double DpiScaleX,
    double DpiScaleY);
