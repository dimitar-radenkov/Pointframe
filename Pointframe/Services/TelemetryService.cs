using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Pointframe.Services;

internal sealed class TelemetryService : ITelemetryService, IDisposable
{
    private readonly ILogger? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private bool _disposed;

    public TelemetryService(IConfiguration configuration)
    {
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

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (_logger is null)
        {
            return;
        }

        if (properties is null or { Count: 0 })
        {
            _logger.LogInformation("{microsoft.custom_event.name}", name);
        }
        else
        {
            using (_logger.BeginScope(properties.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)))
            {
                _logger.LogInformation("{microsoft.custom_event.name}", name);
            }
        }
    }

    public void TrackException(Exception exception, string? context = null)
    {
        if (_logger is null)
        {
            return;
        }

        var props = new Dictionary<string, object?>
        {
            ["exception_type"] = exception.GetType().Name,
        };

        if (context is not null)
        {
            props["context"] = context;
        }

        using (_logger.BeginScope(props))
        {
            _logger.LogError("{microsoft.custom_event.name}", "unhandled_exception");
        }
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
