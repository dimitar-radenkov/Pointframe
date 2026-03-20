using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

public interface IImageFileService
{
    BitmapSource LoadForAnnotation(string path);
}
