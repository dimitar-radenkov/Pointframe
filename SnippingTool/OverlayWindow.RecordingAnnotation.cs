using System.Windows;
using SnippingTool.Models;

namespace SnippingTool;

public partial class OverlayWindow
{
    internal static Int32Rect CalculateRecordingCaptureBounds(Rect windowBounds, BlurShapeParameters parameters, double dpiX, double dpiY)
    {
        return new Int32Rect(
            (int)Math.Round((windowBounds.Left + parameters.Left) * dpiX),
            (int)Math.Round((windowBounds.Top + parameters.Top) * dpiY),
            Math.Max(1, (int)Math.Round(parameters.Width * dpiX)),
            Math.Max(1, (int)Math.Round(parameters.Height * dpiY)));
    }

    internal static Int32Rect CalculateRecordingCaptureBounds(RecordingSessionGeometry geometry, BlurShapeParameters parameters)
    {
        return geometry.MapCaptureLocalDipRectToScreenPixels(new Rect(
            parameters.Left,
            parameters.Top,
            parameters.Width,
            parameters.Height));
    }
}
