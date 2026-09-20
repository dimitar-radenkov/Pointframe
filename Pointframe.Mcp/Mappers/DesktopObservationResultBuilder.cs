using System.Text.Json;
using ModelContextProtocol.Protocol;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp;

/// <summary>
/// Turns a mapped observation into a <see cref="CallToolResult"/> carrying one inline image block per
/// captured rectangle alongside the structured payload. The structured content is unchanged, so an
/// existing reader keeps working; the image blocks are what let the model see the screen at all,
/// which every subsequent click, drag, and scroll depends on.
/// </summary>
internal static class DesktopObservationResultBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static CallToolResult Build(
        DesktopTestingObservationResponse response,
        DesktopObservation? observation,
        bool includeImages)
    {
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = JsonSerializer.Serialize(response, SerializerOptions) },
        };

        if (includeImages && observation is not null)
        {
            // Same order as response.Images, so image_ref N in the structured payload describes the
            // Nth image block. A client that matches them positionally must not be misled.
            foreach (var image in observation.Images)
            {
                if (image.PngBytes is { Length: > 0 } bytes)
                {
                    content.Add(ImageContentBlock.FromBytes(bytes, "image/png"));
                }
            }
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(response, SerializerOptions),
            IsError = response.Error is not null,
        };
    }
}
