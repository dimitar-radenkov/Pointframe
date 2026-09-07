using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Pointframe.Engine;

public sealed class WindowsOcrEngineService : IOcrEngineService
{
    public async Task<string?> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return null;
        }

        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);

        if (result.Lines.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }

    internal static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var converted = bitmap.Clone(bounds, PixelFormat.Format32bppArgb);
        var bitmapData = converted.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var byteCount = bitmapData.Stride * converted.Height;
            var pixels = new byte[byteCount];
            Marshal.Copy(bitmapData.Scan0, pixels, 0, byteCount);

            // GDI+ Format32bppArgb stores bytes as B,G,R,A on little-endian Windows, matching Bgra8 directly.
            var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, converted.Width, converted.Height, BitmapAlphaMode.Ignore);
            softwareBitmap.CopyFromBuffer(pixels.AsBuffer());
            return softwareBitmap;
        }
        finally
        {
            converted.UnlockBits(bitmapData);
        }
    }
}
