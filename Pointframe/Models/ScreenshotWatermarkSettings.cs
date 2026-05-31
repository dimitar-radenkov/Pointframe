namespace Pointframe.Models;

public sealed class ScreenshotWatermarkSettings
{
    public bool Enabled { get; set; }
    public WatermarkTextTemplate TextTemplate { get; set; } = WatermarkTextTemplate.DateTime;
    public WatermarkPosition Position { get; set; } = WatermarkPosition.BottomRight;
    public double FontSize { get; set; } = 18;
    public string ColorHex { get; set; } = "#FFFFFFFF";
    public bool BackgroundEnabled { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public double Margin { get; set; } = 16;
    public bool ApplyToCopy { get; set; } = true;
    public bool ApplyToSave { get; set; } = true;
}
