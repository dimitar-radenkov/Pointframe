namespace Pointframe.Services;

public interface IVideoWriter : IDisposable
{
    void WriteFrame(byte[] frameData);
}
