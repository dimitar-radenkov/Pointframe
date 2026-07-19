using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Pointframe.ViewModels;

namespace Pointframe.Services;

internal sealed class OcrLassoController
{
    private readonly Canvas _canvas;
    private readonly Shape _lassoRect;
    private readonly OverlayViewModel _viewModel;
    private readonly IOcrService _ocrService;
    private readonly ITelemetryService _telemetry;
    private readonly Func<BitmapSource?> _backgroundProvider;
    private readonly Action<string> _showToast;
    private Point? _lassoStart;

    public OcrLassoController(
        Canvas canvas,
        Shape lassoRect,
        OverlayViewModel viewModel,
        IOcrService ocrService,
        ITelemetryService telemetry,
        Func<BitmapSource?> backgroundProvider,
        Action<string> showToast)
    {
        _canvas = canvas;
        _lassoRect = lassoRect;
        _viewModel = viewModel;
        _ocrService = ocrService;
        _telemetry = telemetry;
        _backgroundProvider = backgroundProvider;
        _showToast = showToast;
    }

    public bool HasPendingLasso => _lassoStart.HasValue;

    public bool HandlePointerDown(Point point)
    {
        if (!_viewModel.IsTextLassoActive)
        {
            return false;
        }

        _lassoStart = point;
        var selection = _viewModel.SelectionRect;
        Canvas.SetLeft(_lassoRect, selection.X + point.X);
        Canvas.SetTop(_lassoRect, selection.Y + point.Y);
        _lassoRect.Width = 0;
        _lassoRect.Height = 0;
        _lassoRect.Visibility = Visibility.Visible;
        _canvas.CaptureMouse();
        return true;
    }

    public bool HandlePointerMove(Point point)
    {
        if (!_viewModel.IsTextLassoActive || !_lassoStart.HasValue)
        {
            return false;
        }

        var selection = _viewModel.SelectionRect;
        var x = Math.Min(point.X, _lassoStart.Value.X);
        var y = Math.Min(point.Y, _lassoStart.Value.Y);
        var w = Math.Abs(point.X - _lassoStart.Value.X);
        var h = Math.Abs(point.Y - _lassoStart.Value.Y);
        Canvas.SetLeft(_lassoRect, selection.X + x);
        Canvas.SetTop(_lassoRect, selection.Y + y);
        _lassoRect.Width = w;
        _lassoRect.Height = h;
        return true;
    }

    public bool HandlePointerUp(Point point)
    {
        if (!_viewModel.IsTextLassoActive || !_lassoStart.HasValue)
        {
            return false;
        }

        _canvas.ReleaseMouseCapture();
        var x = Math.Min(point.X, _lassoStart.Value.X);
        var y = Math.Min(point.Y, _lassoStart.Value.Y);
        var w = Math.Abs(point.X - _lassoStart.Value.X);
        var h = Math.Abs(point.Y - _lassoStart.Value.Y);
        _lassoRect.Visibility = Visibility.Collapsed;
        _lassoStart = null;

        if (w >= 4 && h >= 4)
        {
            _ = RecognizeAsync(new Rect(x, y, w, h));
        }

        return true;
    }

    public bool Cancel()
    {
        if (!_viewModel.IsTextLassoActive)
        {
            return false;
        }

        _viewModel.IsTextLassoActive = false;
        _lassoRect.Visibility = Visibility.Collapsed;
        _lassoStart = null;
        return true;
    }

    internal async Task RecognizeAsync(Rect lassoRect)
    {
        var background = _backgroundProvider();
        if (background is null)
        {
            return;
        }

        var pixelX = (int)(lassoRect.X * _viewModel.DpiX);
        var pixelY = (int)(lassoRect.Y * _viewModel.DpiY);
        var pixelW = (int)(lassoRect.Width * _viewModel.DpiX);
        var pixelH = (int)(lassoRect.Height * _viewModel.DpiY);

        pixelX = Math.Max(0, Math.Min(pixelX, background.PixelWidth - 1));
        pixelY = Math.Max(0, Math.Min(pixelY, background.PixelHeight - 1));
        pixelW = Math.Min(pixelW, background.PixelWidth - pixelX);
        pixelH = Math.Min(pixelH, background.PixelHeight - pixelY);

        if (pixelW < 1 || pixelH < 1)
        {
            return;
        }

        var cropped = new CroppedBitmap(background, new Int32Rect(pixelX, pixelY, pixelW, pixelH));
        var ocrProps = new Dictionary<string, string>
        {
            ["selection_width_px"] = pixelW.ToString(),
            ["selection_height_px"] = pixelH.ToString(),
        };

        _telemetry.TrackEvent("ocr_attempted", ocrProps);
        var text = await _ocrService.Recognize(cropped);

        if (string.IsNullOrWhiteSpace(text))
        {
            _telemetry.TrackEvent("ocr_no_text", ocrProps);
            _showToast("No text detected — try a larger area");
            return;
        }

        System.Windows.Clipboard.SetText(text);
        _telemetry.TrackEvent("ocr_used", ocrProps);
        _showToast("✓ Text copied to clipboard");
    }
}
