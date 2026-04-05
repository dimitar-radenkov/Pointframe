using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

public interface IOcrService
{
    Task<string?> Recognize(BitmapSource bitmap);
}
