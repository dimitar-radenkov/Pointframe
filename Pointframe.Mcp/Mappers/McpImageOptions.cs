using ModelContextProtocol.Protocol;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp;

/// <summary>
/// The opt-in knobs that shrink the inline image of a capture tool. The defaults reproduce the original
/// output exactly: a PNG whose longest edge is capped at 1600 px.
/// </summary>
internal sealed record McpImageOptions(int MaxLongestEdge, bool Jpeg)
{
    public const string MaxImageEdgeDescription = "Inline image longest-edge cap in pixels, 64 through 1600 (default 1600). Smaller costs fewer tokens.";

    public const string ImageFormatDescription = "png (default) or jpeg (lossy, smaller for whole-monitor captures). The saved file is always PNG.";

    public static McpImageOptions Default { get; } = new(CapturePreviewImage.DefaultMaxLongestEdgePixels, Jpeg: false);

    public static bool TryCreate(int? maxImageEdge, string? imageFormat, out McpImageOptions options)
    {
        options = Default;
        var edge = maxImageEdge ?? CapturePreviewImage.DefaultMaxLongestEdgePixels;
        if (edge < DesktopTestingLimits.MinImageLongestEdge || edge > CapturePreviewImage.DefaultMaxLongestEdgePixels)
        {
            return false;
        }

        bool jpeg;
        if (imageFormat is null || string.Equals(imageFormat, "png", StringComparison.OrdinalIgnoreCase))
        {
            jpeg = false;
        }
        else if (string.Equals(imageFormat, "jpeg", StringComparison.OrdinalIgnoreCase) || string.Equals(imageFormat, "jpg", StringComparison.OrdinalIgnoreCase))
        {
            jpeg = true;
        }
        else
        {
            return false;
        }

        options = new McpImageOptions(edge, jpeg);
        return true;
    }

    public static CallToolResult InvalidResult()
    {
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "invalid_image_options: maxImageEdge must be 64 through 1600 and imageFormat png or jpeg." }],
        };
    }

    public (byte[] Bytes, string MimeType) Encode(byte[] pngBytes)
    {
        return Jpeg
            ? (CapturePreviewImage.CreateDownscaledJpeg(pngBytes, MaxLongestEdge), "image/jpeg")
            : (CapturePreviewImage.CreateDownscaledPng(pngBytes, MaxLongestEdge), "image/png");
    }
}
