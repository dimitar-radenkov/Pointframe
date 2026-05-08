using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Pointframe.Services;

internal sealed class TelemetryService : ITelemetryService, IDisposable
{
    private readonly ILogger? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IUserSettingsService _userSettings;
    private bool _disposed;

    public TelemetryService(IConfiguration configuration, IUserSettingsService userSettings)
    {
        _userSettings = userSettings;

        var connectionString = configuration["ApplicationInsights:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Trace)
                .AddOpenTelemetry(otel =>
                {
                    otel.AddAzureMonitorLogExporter(options =>
                    {
                        options.ConnectionString = connectionString;
                    });
                });
        });

        _logger = _loggerFactory.CreateLogger("Pointframe.Telemetry");
    }

    internal TelemetryService(ILogger logger, IUserSettingsService userSettings)
    {
        _userSettings = userSettings;
        _logger = logger;
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (_logger is null)
        {
            return;
        }

        var scope = BuildScope(properties);
        using (_logger.BeginScope(scope))
        {
            _logger.LogInformation("{microsoft.custom_event.name}", name);
        }
    }

    public void TrackException(Exception exception, string? context = null)
    {
        if (_logger is null)
        {
            return;
        }

        var extra = new Dictionary<string, string> { ["exception_type"] = exception.GetType().Name };
        if (context is not null)
        {
            extra["context"] = context;
        }

        var scope = BuildScope(extra);
        using (_logger.BeginScope(scope))
        {
            _logger.LogError(exception, "{microsoft.custom_event.name}", "unhandled_exception");
        }
    }

    private Dictionary<string, object?> BuildScope(IReadOnlyDictionary<string, string>? properties)
    {
        var scope = new Dictionary<string, object?>();
        var installId = _userSettings.Current.InstallId;
        if (!string.IsNullOrEmpty(installId))
        {
            scope["install_id"] = installId;
        }

        if (properties is { Count: > 0 })
        {
            foreach (var kvp in properties)
            {
                scope[kvp.Key] = kvp.Value;
            }
        }

        return scope;
    }

    public void Flush()
    {
        // Azure Monitor flushes pending telemetry when the LoggerFactory is disposed.
        // Disposing before host shutdown is handled via IDisposable registration.
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loggerFactory?.Dispose();
    }
}
