namespace Pointframe.Services;

public interface IClipboardService
{
    void SetImage(BitmapSource bitmap);

    void SetText(string text);
}
