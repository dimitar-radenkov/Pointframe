namespace Pointframe.Services;

internal interface IOverlayBitmapCapture
{
    BitmapSource ComposeBitmap(bool restoreOverlayVisibilityAfterCapture = true);
}
