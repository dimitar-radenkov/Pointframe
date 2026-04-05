using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnippingTool;

internal static class SelectionBackdropWindow
{
    private const byte DimOpacity = 128;

    internal static BitmapSource CreateDimmedSnapshot(BitmapSource snapshot)
    {
        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            var bounds = new Rect(0, 0, snapshot.PixelWidth, snapshot.PixelHeight);
            drawingContext.DrawImage(snapshot, bounds);
            drawingContext.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(DimOpacity, 0, 0, 0)),
                null,
                bounds);
        }

        var dimmedSnapshot = new RenderTargetBitmap(
            snapshot.PixelWidth,
            snapshot.PixelHeight,
            snapshot.DpiX,
            snapshot.DpiY,
            PixelFormats.Pbgra32);
        dimmedSnapshot.Render(drawingVisual);
        dimmedSnapshot.Freeze();
        return dimmedSnapshot;
    }
}
