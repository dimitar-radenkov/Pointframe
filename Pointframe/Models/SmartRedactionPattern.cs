namespace Pointframe.Models;

public sealed class SmartRedactionPattern
{
    public const int MaxCount = 20;
    public const int MaxNameLength = 40;
    public const int MaxPatternLength = 300;

    public string Name { get; set; } = "Custom pattern";
    public string Pattern { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
