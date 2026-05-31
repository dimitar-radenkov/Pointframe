using System.Globalization;

namespace Pointframe.Services;

internal static class WatermarkTokenResolver
{
    public static string Resolve(WatermarkTextTemplate template, DateTimeOffset timestamp)
    {
        var culture = CultureInfo.CurrentCulture;
        return template switch
        {
            WatermarkTextTemplate.DateTime => timestamp.ToString("g", culture),
            WatermarkTextTemplate.DateOnly => timestamp.ToString("d", culture),
            WatermarkTextTemplate.TimeOnly => timestamp.ToString("t", culture),
            WatermarkTextTemplate.TimezoneOnly => FormatOffset(timestamp.Offset),
            WatermarkTextTemplate.DateTimeWithTimezone => $"{timestamp.ToString("g", culture)} {FormatOffset(timestamp.Offset)}",
            _ => timestamp.ToString("g", culture),
        };
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}";
    }
}
