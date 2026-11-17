namespace Pointframe.Services;

internal static class CameraFrameConverter
{
    internal const int MaxSourceWidth = 1280;

    // Integer step so a very large camera mode is subsampled before it reaches the UI thread; the bubble is at most a few hundred pixels.
    internal static int GetDecimationStep(int sourceWidth)
    {
        return sourceWidth <= MaxSourceWidth ? 1 : (int)Math.Ceiling(sourceWidth / 640d);
    }

    // Copies a tightly or loosely packed BGRA32 image into a pooled frame, taking every step-th pixel and row.
    internal static CameraFrame CopyDecimated(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride, int step)
    {
        if (step < 1)
        {
            step = 1;
        }

        var width = Math.Max(1, sourceWidth / step);
        var height = Math.Max(1, sourceHeight / step);
        var frame = CameraFrame.Rent(width, height);
        var destination = frame.Pixels.AsSpan(0, frame.Length);

        if (step == 1 && sourceStride == frame.Stride)
        {
            source[..frame.Length].CopyTo(destination);
            return frame;
        }

        for (var row = 0; row < height; row++)
        {
            var sourceRow = source[(row * step * sourceStride)..];
            var destinationRow = destination.Slice(row * frame.Stride, frame.Stride);
            if (step == 1)
            {
                sourceRow[..frame.Stride].CopyTo(destinationRow);
                continue;
            }

            for (var column = 0; column < width; column++)
            {
                sourceRow.Slice(column * step * 4, 4).CopyTo(destinationRow.Slice(column * 4, 4));
            }
        }

        return frame;
    }
}
