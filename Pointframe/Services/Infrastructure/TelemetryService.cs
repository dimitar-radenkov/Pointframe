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
    private readonly string _telemetrySchemaVersion = "1";
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

    public void TrackProductEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        TrackEventInternal(name, properties, TelemetryChannel.Product);
    }

    public void TrackDiagnosticEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        TrackEventInternal(name, properties, TelemetryChannel.Diagnostic);
    }

    public void TrackDiagnosticException(
        Exception exception,
        string? context = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        if (_logger is null || _disposed)
        {
            return;
        }

        var mergedProperties = new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.ExceptionType] = exception.GetType().Name,
        };

        if (context is not null)
        {
            mergedProperties[TelemetryPropertyKeys.Context] = context;
        }

        var lastEvent = _lastEventName;
        if (lastEvent is not null)
        {
            mergedProperties[TelemetryPropertyKeys.LastAction] = lastEvent;
        }

        if (properties is not null)
        {
            foreach (var kvp in properties)
            {
                mergedProperties[kvp.Key] = kvp.Value;
            }
        }

        var validation = TelemetryEventCatalog.Validate(TelemetryEvents.UnhandledException, mergedProperties);
        if (!validation.IsValid)
        {
            LogSchemaValidationFailure(validation);
        }

        var scope = BuildScope(TelemetryChannel.Diagnostic, mergedProperties);
        using (_logger.BeginScope(scope))
        {
            _logger.LogError("{microsoft.custom_event.name}", TelemetryEvents.UnhandledException);
        }
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        TrackProductEvent(name, properties);
    }

    public void TrackException(Exception exception, string? context = null)
    {
        TrackDiagnosticException(exception, context);
    }

    private void TrackEventInternal(string name, IReadOnlyDictionary<string, string>? properties, TelemetryChannel defaultChannel)
    {
        if (_logger is null || _disposed)
        {
            return;
        }

        _lastEventName = name;
        var validation = TelemetryEventCatalog.Validate(name, properties);
        var channel = validation.Definition?.Channel ?? defaultChannel;
        if (!validation.IsValid)
        {
            LogSchemaValidationFailure(validation);
        }

        var scope = BuildScope(channel, properties);
        using (_logger.BeginScope(scope))
        {
            _logger.LogInformation("{microsoft.custom_event.name}", name);
        }
    }

    private Dictionary<string, object?> BuildScope(TelemetryChannel channel, IReadOnlyDictionary<string, string>? properties)
    {
        var scope = new Dictionary<string, object?>
        {
            [TelemetryPropertyKeys.Version] = _appVersion,
            ["session_id"] = _sessionId,
            ["telemetry_channel"] = channel.ToString().ToLowerInvariant(),
            ["telemetry_schema_version"] = _telemetrySchemaVersion,
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

    private void LogSchemaValidationFailure(TelemetrySchemaValidationResult validation)
    {
        if (validation.IsKnownEvent)
        {
            _logger?.LogWarning(
                "Telemetry schema mismatch for {EventName}. Missing required properties: {MissingProperties}",
                validation.EventName,
                string.Join(",", validation.MissingProperties));
            return;
        }

        _logger?.LogWarning("Telemetry event {EventName} is not registered in TelemetryEventCatalog", validation.EventName);
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
