using System.Drawing;
using System.Drawing.Imaging;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopObservationTests
{
    [Fact]
    public async Task ObserveAsync_RunsOcrOnTheCapturedBitmapOnceAtFullResolution()
    {
        var capture = new ChangingCapture();
        var ocr = new BitmapOcr();
        var service = new DesktopObservationService(capture, new DesktopObservationStore(), ocrEngine: ocr);
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");

        var result = await service.ObserveAsync(new DesktopObservationRequest(
            process,
            [new PixelBounds(0, 0, 8, 6), new PixelBounds(10, 20, 2000, 1200)],
            IncludeUiAutomation: false,
            MaxImageLongestEdge: 400,
            OcrImageIndex: 1));

        Assert.Equal(2, capture.CaptureCount);
        Assert.Equal((2000, 1200), ocr.ObservedSize);
        Assert.Equal("2", result.Ocr?.Text);
        Assert.Equal(400, result.Observation.Images[1].Width);
    }

    [Fact]
    public async Task ObserveAsync_WithoutOcrDoesNotCallOcrAndOutOfRangeSkipsIt()
    {
        var capture = new ChangingCapture();
        var ocr = new BitmapOcr();
        var service = new DesktopObservationService(capture, new DesktopObservationStore(), ocrEngine: ocr);
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");

        await service.ObserveAsync(new DesktopObservationRequest(process, [new PixelBounds(0, 0, 4, 3)], IncludeUiAutomation: false));
        var outOfRange = await service.ObserveAsync(new DesktopObservationRequest(
            process, [new PixelBounds(0, 0, 4, 3)], IncludeUiAutomation: false, OcrImageIndex: 1));

        Assert.Equal(2, capture.CaptureCount);
        Assert.Equal(0, ocr.CallCount);
        Assert.Null(outOfRange.Ocr);
    }

    [Fact]
    public async Task ObserveAsync_CapturesEachRectangleOnceWithOrWithoutOcr()
    {
        var capture = new ChangingCapture();
        var ocr = new BitmapOcr();
        var service = new DesktopObservationService(capture, new DesktopObservationStore(), ocrEngine: ocr);
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var bounds = new[] { new PixelBounds(0, 0, 4, 3), new PixelBounds(5, 6, 7, 8) };

        await service.ObserveAsync(new DesktopObservationRequest(process, bounds, IncludeUiAutomation: false));
        Assert.Equal(2, capture.CaptureCount);

        await service.ObserveAsync(new DesktopObservationRequest(
            process, bounds, IncludeUiAutomation: false, OcrImageIndex: 1));

        Assert.Equal(4, capture.CaptureCount);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public async Task ObserveAsync_PassesCancellationTokenToOcr()
    {
        var capture = new ChangingCapture();
        var ocr = new BitmapOcr();
        var service = new DesktopObservationService(capture, new DesktopObservationStore(), ocrEngine: ocr);
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        using var cancellation = new CancellationTokenSource();

        await service.ObserveAsync(new DesktopObservationRequest(
            process, [new PixelBounds(0, 0, 4, 3)], IncludeUiAutomation: false, OcrImageIndex: 0), cancellation.Token);

        Assert.Equal(cancellation.Token, ocr.ObservedToken);
    }

    [Fact]
    public async Task ObserveAsync_RecordsUnavailableWhenOcrWasRequestedWithoutEngine()
    {
        var capture = new ChangingCapture();
        var service = new DesktopObservationService(capture, new DesktopObservationStore());
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");

        var result = await service.ObserveAsync(new DesktopObservationRequest(
            process, [new PixelBounds(0, 0, 4, 3)], IncludeUiAutomation: false, OcrImageIndex: 0));

        Assert.Equal(1, capture.CaptureCount);
        Assert.NotNull(result.Ocr);
        Assert.False(result.Ocr.IsAvailable);
        Assert.Null(result.Ocr.Text);
    }

    [Fact]
    public async Task ObserveAsync_CapturesWithoutUiAutomation()
    {
        var capture = new FakeCapture();
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var store = new DesktopObservationStore();
        var service = new DesktopObservationService(capture, store);

        var result = await service.ObserveAsync(new DesktopObservationRequest(
            process,
            [new PixelBounds(-20, 30, 4, 3)],
            IncludeUiAutomation: true));

        Assert.Equal(DesktopObservationStatus.Available, result.Observation.ObservationStatus);
        Assert.Equal(DesktopUiAutomationStatus.Unavailable, result.Observation.UiaStatus);
        Assert.Equal(new PixelBounds(-20, 30, 4, 3), capture.Bounds);
        Assert.Single(result.Observation.Images);
    }

    private sealed class FakeCapture : IDisplayCaptureEngine
    {
        public PixelBounds Bounds { get; private set; }

        public IReadOnlyList<DisplayDescriptor> GetDisplays() => [];

        public Bitmap Capture(PixelBounds boundsPixels)
        {
            Bounds = boundsPixels;
            return new Bitmap(boundsPixels.Width, boundsPixels.Height);
        }

        public CapturedMonitor CaptureMonitor(string monitorName) => throw new NotSupportedException();
    }

    private sealed class ChangingCapture : IDisplayCaptureEngine
    {
        public int CaptureCount { get; private set; }

        public IReadOnlyList<DisplayDescriptor> GetDisplays() => [];

        public Bitmap Capture(PixelBounds boundsPixels)
        {
            CaptureCount++;
            var bitmap = new Bitmap(boundsPixels.Width, boundsPixels.Height, PixelFormat.Format32bppArgb);
            bitmap.SetPixel(0, 0, Color.FromArgb(CaptureCount, 0, 0));
            return bitmap;
        }

        public CapturedMonitor CaptureMonitor(string monitorName) => throw new NotSupportedException();
    }

    private sealed class BitmapOcr : IOcrEngineService
    {
        public (int Width, int Height) ObservedSize { get; private set; }
        public int CallCount { get; private set; }
        public CancellationToken ObservedToken { get; private set; }

        public Task<string?> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
        {
            CallCount++;
            ObservedSize = (bitmap.Width, bitmap.Height);
            ObservedToken = cancellationToken;
            return Task.FromResult<string?>(bitmap.GetPixel(0, 0).R.ToString());
        }
    }
}
