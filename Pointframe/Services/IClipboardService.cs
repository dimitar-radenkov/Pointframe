using System.Windows.Media.Imaging;

namespace Pointframe.Services;

public interface IClipboardService
{
    void SetImage(BitmapSource bitmap);
}
