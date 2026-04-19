using System.Windows.Media.Imaging;

namespace Pointframe.Services;

public interface IScreenCaptureService
{
    BitmapSource Capture(
        int x,
        int y,
        int width,
        int height);
}
