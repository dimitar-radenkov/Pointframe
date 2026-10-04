using System.Windows;

namespace Pointframe.Services;

internal enum ScrollingCaptureStopReason
{
    EndOfContent,
    NoOverlap,
    FrameLimit,
    HeightLimit,
    Cancelled,
}

internal sealed record ScrollingCaptureResult(
    BitmapSource Image,
    int FrameCount,
    ScrollingCaptureStopReason StopReason,
    int? StopLimit = null);
internal sealed record ScrollingCaptureProgress(int FrameCount, int HeightPixels);

internal interface IScrollingCaptureObserver
{
    Task HideForCaptureAsync(CancellationToken cancellationToken);

    Task ReportProgressAsync(ScrollingCaptureProgress progress, CancellationToken cancellationToken);
}

internal interface IScrollingCaptureService
{
    Task<ScrollingCaptureResult> CaptureAsync(
        Int32Rect regionPixels,
        CancellationToken cancellationToken = default,
        IScrollingCaptureObserver? observer = null);
}
