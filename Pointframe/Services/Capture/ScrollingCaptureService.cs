using System.Windows;

namespace Pointframe.Services;

internal sealed record ScrollingCaptureOptions(TimeSpan SettleDelay, int InitialWheelNotches, int MaxFrames, int MaxHeightPixels)
{
    public static ScrollingCaptureOptions Default { get; } = new(TimeSpan.FromMilliseconds(350), 3, 40, 20000);
}

// Captures a screen region, scrolls the content under it, and stitches frames until the content stops
// moving, no overlap can be found, or a frame or height limit is reached. After the first step the wheel
// step is resized so each scroll moves about 60% of the body, which keeps a solid overlap with fewer frames.
internal sealed class ScrollingCaptureService : IScrollingCaptureService
{
    private const double TargetBodyFractionPerStep = 0.6;
    private const int MaxWheelNotches = 15;

    private readonly IScreenCaptureService _screenCapture;
    private readonly IScrollInputService _scrollInput;
    private readonly ILogger<ScrollingCaptureService> _logger;

    public ScrollingCaptureService(
        IScreenCaptureService screenCapture,
        IScrollInputService scrollInput,
        ILogger<ScrollingCaptureService> logger)
    {
        _screenCapture = screenCapture;
        _scrollInput = scrollInput;
        _logger = logger;
    }

    internal ScrollingCaptureOptions Options { get; init; } = ScrollingCaptureOptions.Default;

    public async Task<ScrollingCaptureResult> CaptureAsync(Int32Rect regionPixels, CancellationToken cancellationToken = default)
    {
        if (regionPixels.Width <= 0 || regionPixels.Height <= 0)
        {
            throw new ArgumentException("The scrolling capture region must have a positive size.", nameof(regionPixels));
        }

        // Let the selection windows disappear before the first frame, or they land in it.
        await DelayAsync(cancellationToken);
        var stitcher = new ScrollingCaptureStitcher(CaptureFrame(regionPixels));
        var frameCount = 1;
        var wheelNotches = Options.InitialWheelNotches;
        var stopReason = ScrollingCaptureStopReason.FrameLimit;

        using (_scrollInput.BeginScrolling(
            regionPixels.X + (regionPixels.Width / 2),
            regionPixels.Y + (regionPixels.Height / 2)))
        {
            while (frameCount < Options.MaxFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _scrollInput.ScrollDown(wheelNotches);
                await DelayAsync(cancellationToken);
                var step = stitcher.Append(CaptureFrame(regionPixels));
                frameCount++;

                if (step.Outcome == ScrollStepOutcome.NoMovement)
                {
                    stopReason = ScrollingCaptureStopReason.EndOfContent;
                    break;
                }

                if (step.Outcome == ScrollStepOutcome.NoOverlap)
                {
                    stopReason = ScrollingCaptureStopReason.NoOverlap;
                    break;
                }

                if (stitcher.Height >= Options.MaxHeightPixels)
                {
                    stopReason = ScrollingCaptureStopReason.HeightLimit;
                    break;
                }

                wheelNotches = NextWheelNotches(step.Shift, wheelNotches, stitcher.BodyHeight);
            }
        }

        _logger.LogInformation(
            "Scrolling capture finished: frames={Frames} height={Height} stop={StopReason}",
            frameCount,
            stitcher.Height,
            stopReason);
        return new ScrollingCaptureResult(stitcher.Build().ToBitmap(), frameCount, stopReason);
    }

    internal static int NextWheelNotches(int shift, int wheelNotches, int bodyHeight)
    {
        var pixelsPerNotch = (double)shift / wheelNotches;
        if (pixelsPerNotch <= 0)
        {
            return wheelNotches;
        }

        var notches = (int)(bodyHeight * TargetBodyFractionPerStep / pixelsPerNotch);
        return Math.Clamp(notches, 1, MaxWheelNotches);
    }

    private PixelFrame CaptureFrame(Int32Rect region)
    {
        return PixelFrame.FromBitmap(_screenCapture.Capture(region.X, region.Y, region.Width, region.Height));
    }

    private Task DelayAsync(CancellationToken cancellationToken)
    {
        return Options.SettleDelay > TimeSpan.Zero
            ? Task.Delay(Options.SettleDelay, cancellationToken)
            : Task.CompletedTask;
    }
}
