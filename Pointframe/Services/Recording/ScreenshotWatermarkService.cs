using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Pointframe.Services.Recording;

public sealed class ScreenshotWatermarkService : IScreenshotWatermarkService
{
    private const double BoxPaddingX = 10d;
    private const double BoxPaddingY = 6d;
    private const double BoxCornerRadius = 6d;

    public BitmapSource Apply(BitmapSource source, ScreenshotWatermarkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        var text = WatermarkTokenResolver.Resolve(settings.TextTemplate, DateTimeOffset.Now);
        if (string.IsNullOrWhiteSpace(text))
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var foreground = ParseColor(settings.ColorHex, Colors.White);
        var fontSize = settings.FontSize > 0d ? settings.FontSize : new ScreenshotWatermarkSettings().FontSize;

        var typeface = new Typeface(
            new System.Windows.Media.FontFamily("Segoe UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);

        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(foreground),
            1.0);

        var boxWidth = formattedText.WidthIncludingTrailingWhitespace + BoxPaddingX * 2;
        var boxHeight = formattedText.Height + BoxPaddingY * 2;
        var (boxX, boxY) = ComputePosition(settings.Position, width, height, boxWidth, boxHeight, settings.Margin);

        var visual = new DrawingVisual();
        using (var drawingContext = visual.RenderOpen())
        {
            drawingContext.DrawImage(source, new Rect(0, 0, width, height));

            drawingContext.PushOpacity(Math.Clamp(settings.Opacity, 0d, 1d));
            if (settings.BackgroundEnabled)
            {
                var boxBrush = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
                drawingContext.DrawRoundedRectangle(
                    boxBrush,
                    null,
                    new Rect(boxX, boxY, boxWidth, boxHeight),
                    BoxCornerRadius,
                    BoxCornerRadius);
            }

            drawingContext.DrawText(formattedText, new Point(boxX + BoxPaddingX, boxY + BoxPaddingY));
            drawingContext.Pop();
        }

        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    internal static (double X, double Y) ComputePosition(
        WatermarkPosition position,
        double canvasWidth,
        double canvasHeight,
        double boxWidth,
        double boxHeight,
        double margin)
    {
        return position switch
        {
            WatermarkPosition.TopLeft => (margin, margin),
            WatermarkPosition.TopRight => (canvasWidth - boxWidth - margin, margin),
            WatermarkPosition.BottomLeft => (margin, canvasHeight - boxHeight - margin),
            WatermarkPosition.BottomRight => (canvasWidth - boxWidth - margin, canvasHeight - boxHeight - margin),
            WatermarkPosition.Center => ((canvasWidth - boxWidth) / 2d, (canvasHeight - boxHeight) / 2d),
            _ => (canvasWidth - boxWidth - margin, canvasHeight - boxHeight - margin),
        };
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && System.Windows.Media.ColorConverter.ConvertFromString(hex) is Color color)
            {
                return color;
            }
        }
        catch (FormatException)
        {
        }

        return fallback;
    }
}
