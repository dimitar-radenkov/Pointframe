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

    // Task.Run cannot terminate a wedged Windows OCR call, so a stuck recognition keeps running (and
    // keeps its SoftwareBitmap alive) after RecognitionTimeout gives up waiting on it. Gating entry
    // here means at most one recognition -- wedged or not -- is ever in flight: a stuck call blocks
    // new work behind this semaphore instead of letting abandoned tasks and native bitmaps pile up
    // without bound.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OcrRecognitionResult> RecognizeDetailedAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        if (!await _gate.WaitAsync(RecognitionTimeout, cancellationToken).ConfigureAwait(false))
        {
            // A previous call is still wedged holding the gate; report failure without starting
            // another Task.Run on top of it.
            return new OcrRecognitionResult(null, OcrRecognitionStatus.Failed);
        }

        // Converted on the calling thread, because the caller's bitmap must not be touched after this
        // method returns. The recognition task then owns the copy and disposes it, which matters on
        // the timeout path: this method returns while that task is still running, so the outer scope
        // must not dispose anything the task is still reading.
        SoftwareBitmap softwareBitmap;
        try
        {
            softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        // Not given cancellationToken: this task must always run to completion so its finally block
        // -- which disposes the bitmap and releases the gate -- always executes, even if the caller's
        // token is already cancelled by the time Task.Run is scheduled.
        var recognition = Task.Run(async () =>
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
                _gate.Release();
            }
        });

        var completed = await Task.WhenAny(recognition, Task.Delay(RecognitionTimeout, cancellationToken)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, recognition))
        {
            // The abandoned task still owns the gate and releases it whenever it eventually finishes
            // (or never does, if truly wedged) -- not here.
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
