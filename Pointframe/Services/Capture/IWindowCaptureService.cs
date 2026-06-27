namespace Pointframe.Services;

internal interface IWindowCaptureService
{
    bool TryCaptureWindowUnderCursor(out BitmapSource? bitmap);
}
