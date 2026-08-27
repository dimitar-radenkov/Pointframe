using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Pointframe.Services;

internal sealed class WindowsOcrService : IOcrService, IOcrRegionService
{
    public async Task<string?> Recognize(BitmapSource bitmap, CancellationToken cancellationToken = default)
    {
        var lines = await RecognizeLines(bitmap, cancellationToken);
        if (lines.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, lines.Select(line => line.Text));
    }

    public async Task<IReadOnlyList<OcrTextLine>> RecognizeLines(BitmapSource bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return [];
        }

        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        if (result.Lines.Count == 0)
        {
            return [];
        }

        var lines = new List<OcrTextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word => new OcrTextWord(
                    word.Text,
                    ClampToPixelBounds(word.BoundingRect, bitmap.PixelWidth, bitmap.PixelHeight)))
                .Where(word => word.PixelBounds.Width > 0 && word.PixelBounds.Height > 0)
                .ToArray();

            if (words.Length == 0)
            {
                continue;
            }

            lines.Add(new OcrTextLine(
                line.Text,
                GetBounds(words),
                words));
        }

        return lines;
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

    private static Int32Rect ClampToPixelBounds(Windows.Foundation.Rect rect, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return Int32Rect.Empty;
        }

        var x = Math.Clamp((int)Math.Floor(rect.X), 0, pixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor(rect.Y), 0, pixelHeight - 1);
        var width = Math.Max(1, (int)Math.Ceiling(rect.Width));
        var height = Math.Max(1, (int)Math.Ceiling(rect.Height));

        width = Math.Min(width, pixelWidth - x);
        height = Math.Min(height, pixelHeight - y);

        return width <= 0 || height <= 0
            ? Int32Rect.Empty
            : new Int32Rect(x, y, width, height);
    }

    private static Int32Rect GetBounds(IReadOnlyList<OcrTextWord> words)
    {
        if (words.Count == 0)
        {
            return Int32Rect.Empty;
        }

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        foreach (var word in words)
        {
            left = Math.Min(left, word.PixelBounds.X);
            top = Math.Min(top, word.PixelBounds.Y);
            right = Math.Max(right, word.PixelBounds.X + word.PixelBounds.Width);
            bottom = Math.Max(bottom, word.PixelBounds.Y + word.PixelBounds.Height);
        }

        return new Int32Rect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }
}
