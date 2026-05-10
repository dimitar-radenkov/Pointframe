using System.Windows.Media.Imaging;

namespace Pointframe.Services;

public sealed class ClipboardService : IClipboardService
{
    public void SetImage(BitmapSource bitmap) => System.Windows.Clipboard.SetImage(bitmap);
}
