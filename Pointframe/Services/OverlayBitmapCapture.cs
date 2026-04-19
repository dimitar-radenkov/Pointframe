using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pointframe.Services;

internal sealed class OverlayBitmapCapture : IOverlayBitmapCapture
{
    private const int CaptureHideDelayMs = 60;

    private readonly Window _overlayWindow;
    private readonly Canvas _annotationCanvas;
    private readonly IScreenCaptureService _screenCapture;
    private readonly Func<Rect> _getSelectionRect;
    private readonly Func<Int32Rect> _getSelectionScreenBoundsPixels;
    private readonly Func<double> _getDpiX;
    private readonly Func<double> _getDpiY;

    public OverlayBitmapCapture(
        Window overlayWindow,
        Canvas annotationCanvas,
        IScreenCaptureService screenCapture,
        Func<Rect> getSelectionRect,
        Func<Int32Rect> getSelectionScreenBoundsPixels,
        Func<double> getDpiX,
        Func<double> getDpiY)
    {
        _overlayWindow = overlayWindow;
        _annotationCanvas = annotationCanvas;
        _screenCapture = screenCapture;
        _getSelectionRect = getSelectionRect;
        _getSelectionScreenBoundsPixels = getSelectionScreenBoundsPixels;
        _getDpiX = getDpiX;
        _getDpiY = getDpiY;
    }

    public BitmapSource ComposeBitmap(bool restoreOverlayVisibilityAfterCapture = true)
    {
        var selectionRect = _getSelectionRect();
        var selectionScreenBoundsPixels = _getSelectionScreenBoundsPixels();
        var screenX = selectionScreenBoundsPixels.X;
        var screenY = selectionScreenBoundsPixels.Y;
        var screenWidth = selectionScreenBoundsPixels.Width;
        var screenHeight = selectionScreenBoundsPixels.Height;

        if (screenWidth <= 0 || screenHeight <= 0)
        {
            var dpiX = _getDpiX();
            var dpiY = _getDpiY();
            screenX = (int)((_overlayWindow.Left + selectionRect.X) * dpiX);
            screenY = (int)((_overlayWindow.Top + selectionRect.Y) * dpiY);
            screenWidth = (int)(selectionRect.Width * dpiX);
            screenHeight = (int)(selectionRect.Height * dpiY);
        }

        var originalVisibility = _overlayWindow.Visibility;
        BitmapSource screenBitmap;

        try
        {
            _overlayWindow.Visibility = Visibility.Hidden;
            FlushDispatcher(_overlayWindow.Dispatcher, DispatcherPriority.Render);
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

        var displayWidth = GetDisplayDimension(_annotationCanvas.Width, _annotationCanvas.ActualWidth);
        var displayHeight = GetDisplayDimension(_annotationCanvas.Height, _annotationCanvas.ActualHeight);
        _annotationCanvas.Measure(new Size(displayWidth, displayHeight));
        _annotationCanvas.Arrange(new Rect(0, 0, displayWidth, displayHeight));
        _annotationCanvas.UpdateLayout();

        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            var targetRect = new Rect(0, 0, screenBitmap.PixelWidth, screenBitmap.PixelHeight);
            drawingContext.DrawImage(screenBitmap, targetRect);

            var annotationBrush = new VisualBrush(_annotationCanvas)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            drawingContext.DrawRectangle(annotationBrush, null, targetRect);
        }

        var finalBitmap = new RenderTargetBitmap(screenBitmap.PixelWidth, screenBitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
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

    private static void FlushDispatcher(Dispatcher dispatcher, DispatcherPriority priority)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            priority,
            new DispatcherOperationCallback(static state =>
            {
                ((DispatcherFrame)state!).Continue = false;
                return null;
            }),
            frame);
        Dispatcher.PushFrame(frame);
    }
}
