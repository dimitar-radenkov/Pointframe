namespace Pointframe.Services;

public interface IScreenshotWatermarkService
{
    BitmapSource Apply(BitmapSource source, ScreenshotWatermarkSettings settings);
}
