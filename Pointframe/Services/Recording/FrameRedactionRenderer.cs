using Pointframe.Engine;

namespace Pointframe.Services;

public sealed class FrameRedactionRenderer : IFrameRedactionRenderer
{
    private const int PixelBlockSize = 8;

    public void Render(
        byte[] frameData,
        int frameWidth,
        int frameHeight,
        ReadOnlySpan<RecordingRedactionRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(frameData);
        if (frameWidth < 0 || frameHeight < 0 || frameData.Length < frameWidth * frameHeight * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(frameData));
        }

        foreach (var region in regions)
        {
            if (region.Mode == RecordingRedactionMode.Pixelate)
            {
                RawFramePixelation.Render(
                    frameData,
                    frameWidth,
                    frameHeight,
                    [new PixelBounds(
                        region.CaptureLocalBounds.X,
                        region.CaptureLocalBounds.Y,
                        region.CaptureLocalBounds.Width,
                        region.CaptureLocalBounds.Height)]);
            }
        }
    }
}
