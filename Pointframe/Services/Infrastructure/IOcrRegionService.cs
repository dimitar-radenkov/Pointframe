using System.Windows;

namespace Pointframe.Services;

internal interface IOcrRegionService
{
    Task<IReadOnlyList<OcrTextLine>> RecognizeLines(BitmapSource bitmap, CancellationToken cancellationToken = default);
}

internal sealed record OcrTextLine(
    string Text,
    Int32Rect PixelBounds,
    IReadOnlyList<OcrTextWord> Words);

internal sealed record OcrTextWord(
    string Text,
    Int32Rect PixelBounds);
