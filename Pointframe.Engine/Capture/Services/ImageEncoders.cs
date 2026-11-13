using System.Drawing;
using System.Drawing.Imaging;

namespace Pointframe.Engine;

public static class ImageEncoders
{
    private static readonly Lazy<ImageCodecInfo[]> _encoders = new(
        ImageCodecInfo.GetImageEncoders,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImageCodecInfo> _png = new(
        () => Find(ImageFormat.Png, "PNG"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImageCodecInfo> _jpeg = new(
        () => Find(ImageFormat.Jpeg, "JPEG"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static ImageCodecInfo Png => _png.Value;

    public static ImageCodecInfo Jpeg => _jpeg.Value;

    public static void SavePng(Image image, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(stream);
        image.Save(stream, Png, null);
    }

    public static void SavePng(Image image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        image.Save(path, Png, null);
    }

    private static ImageCodecInfo Find(ImageFormat format, string name)
    {
        var codec = _encoders.Value.FirstOrDefault(encoder => encoder.FormatID == format.Guid);
        return codec ?? throw new InvalidOperationException($"GDI+ has no {name} encoder.");
    }
}
