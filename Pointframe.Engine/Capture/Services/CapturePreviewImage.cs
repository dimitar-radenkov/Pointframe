using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Pointframe.Engine;

/// <summary>
/// Produces a bounded-size PNG copy of a saved capture, suitable for returning inline to an MCP
/// client that needs to actually see the image rather than a file path it cannot open. The on-disk
/// artifact is always the full-resolution one; this is only the transport-friendly preview.
/// </summary>
public static class CapturePreviewImage
{
    // A 4K monitor PNG is several megabytes of base64 once inlined; 1600 px keeps a whole-monitor
    // capture legible while staying well inside typical MCP client payload limits. This matches the
    // desktop-testing driver's returned-image longest-edge cap.
    public const int DefaultMaxLongestEdgePixels = 1600;

    /// <summary>
    /// Reads the PNG at <paramref name="pngPath"/> and returns PNG bytes whose longest edge is at
    /// most <paramref name="maxLongestEdgePixels"/>. When the source already fits, the original bytes
    /// are returned unchanged; otherwise it is downscaled with high-quality interpolation, preserving
    /// aspect ratio.
    /// </summary>
    public static byte[] CreateDownscaledPng(string pngPath, int maxLongestEdgePixels = DefaultMaxLongestEdgePixels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pngPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLongestEdgePixels);

        var originalBytes = File.ReadAllBytes(pngPath);

        // GDI+ keeps the stream open for the lifetime of the Bitmap, so it must outlive every use.
        using var sourceStream = new MemoryStream(originalBytes, writable: false);
        using var source = new Bitmap(sourceStream);

        var longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maxLongestEdgePixels)
        {
            return originalBytes;
        }

        var scale = (double)maxLongestEdgePixels / longestEdge;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        using var output = new MemoryStream();
        resized.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
