namespace SnippingTool.Models;

public sealed class UserSettings
{
    public string ScreenshotSavePath { get; set; } =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnippingTool", "Screenshots");

    public bool AutoSaveScreenshots { get; set; } = false;

    public string RecordingOutputPath { get; set; } =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnippingTool", "Videos");

    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.Mp4;
    public int RecordingFps { get; set; } = 20;
    public int RecordingJpegQuality { get; set; } = 85;
    public int HudCloseDelaySeconds { get; set; } = 2;
    public int HudGapPixels { get; set; } = 8;

    public string DefaultAnnotationColor { get; set; } = "#FFFF0000";
    public double DefaultStrokeThickness { get; set; } = 2.5;
    public int CaptureDelaySeconds { get; set; } = 0;
}
