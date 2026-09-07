using System.Drawing;

namespace Pointframe.Engine;

public interface IDisplayCaptureEngine
{
    IReadOnlyList<DisplayDescriptor> GetDisplays();

    Bitmap Capture(PixelBounds boundsPixels);

    CapturedMonitor CaptureMonitor(string monitorName);
}
