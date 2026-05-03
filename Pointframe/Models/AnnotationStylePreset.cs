namespace Pointframe.Models;

public sealed class AnnotationStylePreset
{
    public const int MaxNameLength = 24;
    public const int MaxCount = 5;

    public string Name { get; set; } = "Preset";
    public string Color { get; set; } = "#FFFF0000";
    public double StrokeThickness { get; set; } = 2.5;
}
