using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

public sealed class ClipboardService : IClipboardService
{
    public void SetImage(BitmapSource bitmap) => System.Windows.Clipboard.SetImage(bitmap);
}
