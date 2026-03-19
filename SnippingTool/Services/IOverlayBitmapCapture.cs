using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

internal interface IOverlayBitmapCapture
{
    BitmapSource ComposeBitmap();
}
