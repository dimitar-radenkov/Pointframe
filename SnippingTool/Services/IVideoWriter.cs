namespace SnippingTool.Services;

public interface IVideoWriter : IDisposable
{
    void WriteFrame(byte[] frameData);
}
