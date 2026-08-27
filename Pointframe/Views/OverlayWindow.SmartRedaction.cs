using System.Windows;
using System.Windows.Threading;

namespace Pointframe;

public partial class OverlayWindow
{
    private const int SmartRedactionPaddingPixels = 8;

    private async void SmartRedact_Click(object sender, RoutedEventArgs e) => await ApplySmartRedactionAsync();

    private async Task ApplySmartRedactionAsync()
    {
        if (!_vm.IsSmartRedactionEnabled)
        {
            ShowOcrToast("Smart redaction is disabled in Settings");
            return;
        }

        if (_isSmartRedactionInProgress)
        {
            return;
        }

        var background = _renderer.BackgroundCapture;
        if (background is null)
        {
            ShowOcrToast("No capture available for smart redaction");
            return;
        }

        _isSmartRedactionInProgress = true;
        try
        {
            ShowOcrToast("Scanning for sensitive data...");
            var suggestions = await _smartRedactionService.DetectAsync(background);
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (Dispatcher.CheckAccess())
            {
                ApplySmartRedactionSuggestions(suggestions, background);
            }
            else
            {
                await Dispatcher.InvokeAsync(
                    () => ApplySmartRedactionSuggestions(suggestions, background),
                    DispatcherPriority.Normal);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Smart redaction canceled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smart redaction failed");
            ShowOcrToast("Smart redaction failed");
        }
        finally
        {
            _isSmartRedactionInProgress = false;
        }
    }

    private void ApplySmartRedactionSuggestions(
        IReadOnlyList<SmartRedactionSuggestion> suggestions,
        BitmapSource background)
    {
        var suggestionRects = suggestions
            .Select(suggestion => TryConvertToDipRect(suggestion.PixelBounds, background.PixelWidth, background.PixelHeight))
            .Where(rect => rect.HasValue)
            .Select(rect => rect.GetValueOrDefault())
            .ToArray();

        if (suggestionRects.Length == 0)
        {
            ShowOcrToast("No sensitive data found");
            return;
        }

        _vm.BeginGroup();
        var committed = _renderer.CommitBlurSuggestions(suggestionRects);
        _vm.CommitGroup();

        if (committed.Count == 0)
        {
            ShowOcrToast("No sensitive data found");
            return;
        }

        ShowOcrToast($"Applied {committed.Count} redactions");
    }

    private Rect? TryConvertToDipRect(Int32Rect pixelBounds, int imagePixelWidth, int imagePixelHeight)
    {
        var expandedPixelBounds = ExpandPixelBounds(pixelBounds, SmartRedactionPaddingPixels);
        var clampedPixelBounds = ClampPixelBounds(expandedPixelBounds, imagePixelWidth, imagePixelHeight);
        if (clampedPixelBounds.Width <= 0 || clampedPixelBounds.Height <= 0)
        {
            return null;
        }

        var dpiX = _vm.DpiX > 0d ? _vm.DpiX : 1d;
        var dpiY = _vm.DpiY > 0d ? _vm.DpiY : 1d;

        var rect = new Rect(
            clampedPixelBounds.X / dpiX,
            clampedPixelBounds.Y / dpiY,
            clampedPixelBounds.Width / dpiX,
            clampedPixelBounds.Height / dpiY);

        if (rect.Width < 1d || rect.Height < 1d)
        {
            return null;
        }

        return rect;
    }

    private static Int32Rect ExpandPixelBounds(Int32Rect pixelBounds, int paddingPixels)
    {
        if (paddingPixels <= 0)
        {
            return pixelBounds;
        }

        var paddedWidth = Math.Max(1, pixelBounds.Width + (paddingPixels * 2));
        var paddedHeight = Math.Max(1, pixelBounds.Height + (paddingPixels * 2));
        return new Int32Rect(
            pixelBounds.X - paddingPixels,
            pixelBounds.Y - paddingPixels,
            paddedWidth,
            paddedHeight);
    }

    private static Int32Rect ClampPixelBounds(Int32Rect pixelBounds, int imagePixelWidth, int imagePixelHeight)
    {
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            return Int32Rect.Empty;
        }

        var left = pixelBounds.X;
        var top = pixelBounds.Y;
        var right = (long)pixelBounds.X + Math.Max(1, pixelBounds.Width);
        var bottom = (long)pixelBounds.Y + Math.Max(1, pixelBounds.Height);

        var clampedLeft = Math.Clamp(left, 0, imagePixelWidth);
        var clampedTop = Math.Clamp(top, 0, imagePixelHeight);
        var clampedRight = Math.Clamp(right, 0L, imagePixelWidth);
        var clampedBottom = Math.Clamp(bottom, 0L, imagePixelHeight);

        var width = (int)(clampedRight - clampedLeft);
        var height = (int)(clampedBottom - clampedTop);

        return width <= 0 || height <= 0
            ? Int32Rect.Empty
            : new Int32Rect(clampedLeft, clampedTop, width, height);
    }
}
