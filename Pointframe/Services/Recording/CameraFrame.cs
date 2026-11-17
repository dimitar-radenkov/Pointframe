using System.Buffers;

namespace Pointframe.Services;

// One BGRA32 camera frame in a pooled buffer. The owner disposes it exactly once; Dispose is idempotent.
public sealed class CameraFrame : IDisposable
{
    private byte[]? _pixels;

    public CameraFrame(byte[] pooledPixels, int width, int height, int stride)
    {
        _pixels = pooledPixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int Length => Stride * Height;

    public bool IsDisposed => _pixels is null;

    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CameraFrame));

    public static CameraFrame Rent(int width, int height)
    {
        var stride = width * 4;
        return new CameraFrame(ArrayPool<byte>.Shared.Rent(stride * height), width, height, stride);
    }

    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }
}
