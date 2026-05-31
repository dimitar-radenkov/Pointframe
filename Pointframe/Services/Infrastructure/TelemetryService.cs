using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;

namespace Pointframe.Services;

internal sealed class TelemetryService : ITelemetryService, IDisposable
{
    private readonly ILogger? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IUserSettingsService _userSettings;
    private readonly string _appVersion;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly object _syncRoot = new();
    private volatile string? _lastEventName;
    private bool _disposed;

    public TelemetryService(
        IConfiguration configuration,
        IUserSettingsService userSettings,
        IAppVersionService appVersionService)
    {
        _userSettings = userSettings;
        _appVersion = appVersionService.Current.ToString();

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
                    otel.IncludeScopes = true;
                    otel.AddAzureMonitorLogExporter(options =>
                    {
                        options.ConnectionString = connectionString;
                    });
                });
        });

        _logger = _loggerFactory.CreateLogger("Pointframe.Telemetry");
    }

    internal TelemetryService(
        ILogger logger,
        IUserSettingsService userSettings,
        IAppVersionService appVersionService)
    {
        _userSettings = userSettings;
        _appVersion = appVersionService.Current.ToString();
        _logger = logger;
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (_logger is null || _disposed)
        {
            return;
        }

        _lastEventName = name;
        var scope = BuildScope(properties);
        using (_logger.BeginScope(scope))
        {
            _logger.LogInformation("{microsoft.custom_event.name}", name);
        }
    }

    public void TrackException(Exception exception, string? context = null)
    {
        if (_logger is null || _disposed)
        {
            return;
        }

        var extra = new Dictionary<string, string> { ["exception_type"] = exception.GetType().Name };
        if (context is not null)
        {
            extra["context"] = context;
        }

        var lastEvent = _lastEventName;
        if (lastEvent is not null)
        {
            extra["last_action"] = lastEvent;
        }

        var scope = BuildScope(extra);
        using (_logger.BeginScope(scope))
        {
            _logger.LogError("{microsoft.custom_event.name}", "unhandled_exception");
        }
    }

    private Dictionary<string, object?> BuildScope(IReadOnlyDictionary<string, string>? properties)
    {
        var scope = new Dictionary<string, object?>
        {
            ["version"] = _appVersion,
            ["session_id"] = _sessionId,
        };

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
        Dispose();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _loggerFactory?.Dispose();
        }
    }
}
