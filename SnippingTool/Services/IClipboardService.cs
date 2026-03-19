using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

public interface IClipboardService
{
    void SetImage(BitmapSource bitmap);
}
