using System.Globalization;

namespace Pointframe.Services;

internal static class WatermarkTokenResolver
{
    public static string Resolve(string template, DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var culture = CultureInfo.CurrentCulture;
        return template
            .Replace("{datetime}", timestamp.ToString("g", culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", timestamp.ToString("d", culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", timestamp.ToString("t", culture), StringComparison.OrdinalIgnoreCase)
            .Replace("{timezone}", FormatOffset(timestamp.Offset), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}";
    }
}
