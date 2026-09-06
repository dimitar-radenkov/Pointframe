namespace Pointframe.Services;

public interface IFrameRedactionRenderer
{
    void Render(
        byte[] frameData,
        int frameWidth,
        int frameHeight,
        ReadOnlySpan<RecordingRedactionRegion> regions);
}
