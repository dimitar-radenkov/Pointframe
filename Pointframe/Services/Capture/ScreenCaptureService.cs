using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Pointframe.Services;

namespace Pointframe;

internal sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly ILogger<ScreenCaptureService> _logger;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public ScreenCaptureService(ILogger<ScreenCaptureService> logger)
    {
        _logger = logger;
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
            using var bmp = new System.Drawing.Bitmap(
                width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0,
                new System.Drawing.Size(width, height),
                System.Drawing.CopyPixelOperation.SourceCopy);

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
