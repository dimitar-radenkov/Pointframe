using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

internal sealed class OverlayBitmapCapture : IOverlayBitmapCapture
{
    private const int CaptureHideDelayMs = 60;

    private readonly Window _overlayWindow;
    private readonly Canvas _annotationCanvas;
    private readonly IScreenCaptureService _screenCapture;
    private readonly Func<Rect> _getSelectionRect;
    private readonly Func<double> _getDpiX;
    private readonly Func<double> _getDpiY;

    public OverlayBitmapCapture(
        Window overlayWindow,
        Canvas annotationCanvas,
        IScreenCaptureService screenCapture,
        Func<Rect> getSelectionRect,
        Func<double> getDpiX,
        Func<double> getDpiY)
    {
        _overlayWindow = overlayWindow;
        _annotationCanvas = annotationCanvas;
        _screenCapture = screenCapture;
        _getSelectionRect = getSelectionRect;
        _getDpiX = getDpiX;
        _getDpiY = getDpiY;
    }

    public BitmapSource ComposeBitmap(bool restoreOverlayVisibilityAfterCapture = true)
    {
        var selectionRect = _getSelectionRect();
        var dpiX = _getDpiX();
        var dpiY = _getDpiY();
        var screenX = (int)((_overlayWindow.Left + selectionRect.X) * dpiX);
        var screenY = (int)((_overlayWindow.Top + selectionRect.Y) * dpiY);
        var screenWidth = (int)(selectionRect.Width * dpiX);
        var screenHeight = (int)(selectionRect.Height * dpiY);

        var originalVisibility = _overlayWindow.Visibility;
        BitmapSource screenBitmap;

        try
        {
            _overlayWindow.Visibility = Visibility.Hidden;
            System.Threading.Thread.Sleep(CaptureHideDelayMs);
            screenBitmap = _screenCapture.Capture(screenX, screenY, screenWidth, screenHeight);
        }
        finally
        {
            if (restoreOverlayVisibilityAfterCapture)
            {
                _overlayWindow.Visibility = originalVisibility;
            }
        }

        var annotationBitmap = new RenderTargetBitmap(screenWidth, screenHeight, 96 * dpiX, 96 * dpiY, PixelFormats.Pbgra32);
        annotationBitmap.Render(_annotationCanvas);

        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            var targetRect = new Rect(0, 0, screenBitmap.PixelWidth, screenBitmap.PixelHeight);
            drawingContext.DrawImage(screenBitmap, targetRect);
            drawingContext.DrawImage(annotationBitmap, targetRect);
        }

        var finalBitmap = new RenderTargetBitmap(screenBitmap.PixelWidth, screenBitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        finalBitmap.Render(drawingVisual);
        finalBitmap.Freeze();
        return finalBitmap;
    }
}
