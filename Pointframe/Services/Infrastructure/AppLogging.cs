using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace Pointframe.Services;

public static class AppLogging
{
    public const long DefaultFileSizeLimitBytes = 20L * 1024 * 1024;
    public const int DefaultRetainedFileCount = 10;

    public static LoggerConfiguration CreateConfiguration(IConfiguration config, string path, LogEventLevel defaultLevel)
    {
        var loggerConfiguration = new LoggerConfiguration().MinimumLevel.Is(defaultLevel);

        foreach (var entry in config.GetSection("Logging:MinimumLevelOverrides").GetChildren())
        {
            if (Enum.TryParse<LogEventLevel>(entry.Value, ignoreCase: true, out var level))
            {
                loggerConfiguration.MinimumLevel.Override(entry.Key, level);
            }
        }

        return loggerConfiguration.WriteTo.File(
            path,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: PositiveOrDefault(config["Logging:FileSizeLimitBytes"], DefaultFileSizeLimitBytes),
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: (int)PositiveOrDefault(config["Logging:RetainedFileCountLimit"], DefaultRetainedFileCount, int.MaxValue),
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    }

    // A malformed or non-positive value must not stop the app from starting; it falls back to the default cap.
    internal static long PositiveOrDefault(string? value, long defaultValue, long maxValue = long.MaxValue)
    {
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            && parsed <= maxValue
                ? parsed
                : defaultValue;
    }
}
