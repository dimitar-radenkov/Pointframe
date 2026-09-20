using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Pointframe.Engine;

public sealed class WindowsOcrEngineService : IOcrEngineService
{
    // The Windows OCR stack can wedge indefinitely -- observed on a machine where a 400x200 region
    // never returned, taking the whole host process with it. Neither TryCreateFromUserProfileLanguages
    // nor RecognizeAsync is reliably cancellable, so bound the wait and report a failure instead of
    // hanging every caller behind it.
    public static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(30);

    public async Task<OcrRecognitionResult> RecognizeDetailedAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        // Converted on the calling thread, because the caller's bitmap must not be touched after this
        // method returns. The recognition task then owns the copy and disposes it, which matters on
        // the timeout path: this method returns while that task is still running, so the outer scope
        // must not dispose anything the task is still reading.
        var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var recognition = Task.Run(
            async () =>
            {
                try
                {
                    var engine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (engine is null)
                    {
                        return OcrRecognitionResult.Unavailable;
                    }

                    var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
                    return result.Lines.Count == 0
                        ? OcrRecognitionResult.NoText
                        : OcrRecognitionResult.Recognized(string.Join(Environment.NewLine, result.Lines.Select(line => line.Text)));
                }
                finally
                {
                    softwareBitmap.Dispose();
                }
            },
            cancellationToken);

        var completed = await Task.WhenAny(recognition, Task.Delay(RecognitionTimeout, cancellationToken)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, recognition))
        {
            // The task owns and disposes the software bitmap, so abandoning it here leaks nothing.
            // What matters is that the caller is released rather than blocked forever.
            return new OcrRecognitionResult(null, OcrRecognitionStatus.Failed);
        }

        return await recognition.ConfigureAwait(false);
    }

    public async Task<string?> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        return (await RecognizeDetailedAsync(bitmap, cancellationToken).ConfigureAwait(false)).Text;
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
