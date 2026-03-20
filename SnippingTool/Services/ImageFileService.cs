using System.IO;
using System.Windows.Media.Imaging;

namespace SnippingTool.Services;

public sealed class ImageFileService : IImageFileService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp"
    };

    public BitmapSource LoadForAnnotation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException($"Unsupported image format '{extension}'. Supported formats are PNG, JPG, JPEG, and BMP.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected image file was not found.", path);
        }

        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            throw new InvalidDataException("The selected image file could not be opened as a supported bitmap.", ex);
        }
    }
}
