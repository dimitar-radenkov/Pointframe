using System.Windows.Media.Imaging;

namespace Pointframe.Services;

public interface IImageFileService
{
    BitmapSource LoadForAnnotation(string path);
}
