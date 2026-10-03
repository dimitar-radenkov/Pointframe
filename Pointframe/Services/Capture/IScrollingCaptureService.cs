using System.Windows;

namespace Pointframe.Services;

internal enum ScrollingCaptureStopReason
{
    EndOfContent,
    NoOverlap,
    FrameLimit,
    HeightLimit,
}

internal sealed record ScrollingCaptureResult(BitmapSource Image, int FrameCount, ScrollingCaptureStopReason StopReason);

internal interface IScrollingCaptureService
{
    Task<ScrollingCaptureResult> CaptureAsync(Int32Rect regionPixels, CancellationToken cancellationToken = default);
}
