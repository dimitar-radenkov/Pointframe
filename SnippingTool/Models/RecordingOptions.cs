namespace SnippingTool.Models;

public sealed class RecordingOptions
{
    public const string Section = "Recording";

    public int Fps { get; init; } = 20;
    public int JpegQuality { get; init; } = 85;
    public string OutputSubfolder { get; init; } = "Videos";
    public int HudCloseDelaySeconds { get; init; } = 2;
    public int HudGapPixels { get; init; } = 8;
}
