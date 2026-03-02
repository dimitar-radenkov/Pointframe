using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SnippingTool.Services;

namespace SnippingTool;

internal sealed class ScreenCaptureService : IScreenCaptureService
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public BitmapSource Capture(int x, int y, int width, int height)
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
            return Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}