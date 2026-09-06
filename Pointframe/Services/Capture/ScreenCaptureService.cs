using System.Windows;
using System.Windows.Interop;
using Pointframe.Engine;
using Pointframe.Services;

namespace Pointframe;

internal sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<ScreenCaptureService> _logger;
    private readonly IDisplayCaptureEngine _displayCaptureEngine;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public ScreenCaptureService(
        ILogger<ScreenCaptureService> logger,
        IDisplayCaptureEngine displayCaptureEngine)
    {
        _logger = logger;
        _displayCaptureEngine = displayCaptureEngine;
    }

    public BitmapSource Capture(
        int x,
        int y,
        int width,
        int height)
    {
        _logger.LogInformation("Capture started: ({X},{Y}) {W}\u00d7{H}", x, y, width, height);
        try
        {
            using var bmp = _displayCaptureEngine.Capture(new PixelBounds(x, y, width, height));

            var hBitmap = bmp.GetHbitmap();
            try
            {
                var result = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                _logger.LogInformation("Capture completed: {W}\u00d7{H}", width, height);
                return result;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture failed at ({X},{Y}) {W}\u00d7{H}", x, y, width, height);
            throw;
        }
    }
}
