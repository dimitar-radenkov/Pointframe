using System.Windows.Media;

namespace Pointframe.Services;

internal static class BeautifyPresets
{
    public static System.Windows.Media.Brush GetBrush(BeautifyBackground preset) => preset switch
    {
        BeautifyBackground.Sunset => Frozen(Gradient("#F97316", "#EC4899", "#8B5CF6")),
        BeautifyBackground.Ocean => Frozen(Gradient("#0EA5E9", "#06B6D4", "#10B981")),
        BeautifyBackground.Forest => Frozen(Gradient("#166534", "#15803D", "#4ADE80")),
        BeautifyBackground.Lavender => Frozen(Gradient("#7C3AED", "#A78BFA", "#C084FC")),
        BeautifyBackground.Slate => Frozen(Gradient("#334155", "#475569", "#64748B")),
        BeautifyBackground.Rose => Frozen(Gradient("#BE123C", "#F43F5E", "#FB923C")),
        BeautifyBackground.White => Frozen(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC))),
        _ => Brushes.Transparent,
    };

    private static LinearGradientBrush Gradient(params string[] hexColors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
        };
        for (var i = 0; i < hexColors.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(ParseHex(hexColors[i]), (double)i / (hexColors.Length - 1)));
        }

        return brush;
    }

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
