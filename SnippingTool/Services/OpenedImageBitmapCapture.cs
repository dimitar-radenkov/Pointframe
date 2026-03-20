using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

internal sealed class OpenedImageBitmapCapture : IOverlayBitmapCapture
{
    private readonly BitmapSource _sourceBitmap;
    private readonly Canvas _annotationCanvas;

    public OpenedImageBitmapCapture(BitmapSource sourceBitmap, Canvas annotationCanvas)
    {
        _sourceBitmap = sourceBitmap;
        _annotationCanvas = annotationCanvas;
    }

    public BitmapSource ComposeBitmap(bool restoreOverlayVisibilityAfterCapture = true)
    {
        var displayWidth = GetDisplayDimension(_annotationCanvas.Width, _annotationCanvas.ActualWidth);
        var displayHeight = GetDisplayDimension(_annotationCanvas.Height, _annotationCanvas.ActualHeight);
        _annotationCanvas.Measure(new Size(displayWidth, displayHeight));
        _annotationCanvas.Arrange(new Rect(0, 0, displayWidth, displayHeight));
        _annotationCanvas.UpdateLayout();

        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            var targetRect = new Rect(0, 0, _sourceBitmap.PixelWidth, _sourceBitmap.PixelHeight);
            drawingContext.DrawImage(_sourceBitmap, targetRect);

            var annotationBrush = new VisualBrush(_annotationCanvas)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            drawingContext.DrawRectangle(annotationBrush, null, targetRect);
        }

        var dpiX = _sourceBitmap.DpiX > 0 ? _sourceBitmap.DpiX : 96;
        var dpiY = _sourceBitmap.DpiY > 0 ? _sourceBitmap.DpiY : 96;
        var finalBitmap = new RenderTargetBitmap(
            _sourceBitmap.PixelWidth,
            _sourceBitmap.PixelHeight,
            dpiX,
            dpiY,
            PixelFormats.Pbgra32);
        finalBitmap.Render(drawingVisual);
        finalBitmap.Freeze();
        return finalBitmap;
    }

    private static double GetDisplayDimension(double preferredValue, double actualValue)
    {
        if (preferredValue > 0)
        {
            return preferredValue;
        }

        if (actualValue > 0)
        {
            return actualValue;
        }

        return 1d;
    }
}
