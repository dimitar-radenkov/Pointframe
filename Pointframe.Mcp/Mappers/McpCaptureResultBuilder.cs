using System.Text.Json;
using ModelContextProtocol.Protocol;
using Pointframe.Engine;

namespace Pointframe.Mcp;

/// <summary>
/// Turns a mapped <see cref="McpCaptureResponse"/> into a <see cref="CallToolResult"/> that carries
/// both the structured metadata (as before) and, unless the caller opted out, the captured image as
/// an inline image content block so the calling model can actually see it instead of only a path.
/// </summary>
internal static class McpCaptureResultBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult Build(McpCaptureResponse response, bool includeImage)
    {
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = JsonSerializer.Serialize(response, SerializerOptions) },
        };

        if (includeImage && response is { Success: true, Artifact.Metadata.Path: { Length: > 0 } path } && File.Exists(path))
        {
            content.Add(ImageContentBlock.FromBytes(CapturePreviewImage.CreateDownscaledPng(path), "image/png"));
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(response, SerializerOptions),
            IsError = !response.Success,
        };
    }
}
