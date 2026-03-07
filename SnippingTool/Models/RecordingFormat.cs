namespace SnippingTool.Models;

public enum RecordingFormat
{
    Mp4,
    Avi
}

public static class RecordingFormatValues
{
    public static RecordingFormat[] All { get; } = Enum.GetValues<RecordingFormat>();
}
