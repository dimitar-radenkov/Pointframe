using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Pointframe.Services;

internal sealed class WindowsOcrService : IOcrService
{
    public async Task<string?> Recognize(BitmapSource bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return null;
        }

        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(softwareBitmap);

        if (result.Lines.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    private static SoftwareBitmap ConvertToSoftwareBitmap(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            bgra.PixelWidth,
            bgra.PixelHeight,
            BitmapAlphaMode.Premultiplied);

        softwareBitmap.CopyFromBuffer(pixels.AsBuffer());
        return softwareBitmap;
    }
}
