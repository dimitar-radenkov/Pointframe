using System.Windows.Media.Imaging;

namespace Pointframe.Services;

public interface IOcrService
{
    Task<string?> Recognize(BitmapSource bitmap);
}
