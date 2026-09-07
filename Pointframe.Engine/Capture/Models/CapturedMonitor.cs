using System.Drawing;

namespace Pointframe.Engine;

public sealed class CapturedMonitor(DisplayDescriptor Display, Bitmap Bitmap) : IDisposable
{
    public DisplayDescriptor Display { get; } = Display;

    public Bitmap Bitmap { get; } = Bitmap;

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
