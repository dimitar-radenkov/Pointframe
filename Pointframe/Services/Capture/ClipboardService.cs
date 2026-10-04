namespace Pointframe.Services;

public sealed class ClipboardService : IClipboardService
{
    public void SetImage(BitmapSource bitmap) => System.Windows.Clipboard.SetImage(bitmap);

    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
