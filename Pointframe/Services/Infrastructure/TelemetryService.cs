using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Pointframe.Services;

internal sealed class TelemetryService : ITelemetryService, IDisposable
{
    private const int FlushTimeoutMilliseconds = 5000;
    private const int MaxQueueSize = 2048;
    private const int ScheduledDelayMilliseconds = 5000;
    private const int ExporterTimeoutMilliseconds = 30000;
    private const int MaxExportBatchSize = 512;

    private readonly ILogger? _logger;
    private readonly ILogger<TelemetryService> _localLogger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly BaseProcessor<LogRecord>? _exportProcessor;
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
        IAppVersionService appVersionService,
        ILogger<TelemetryService> localLogger)
    {
        _userSettings = userSettings;
        _appVersion = appVersionService.Current.ToString();
        _localLogger = localLogger;

        var connectionString = configuration["ApplicationInsights:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var exporter = new AzureMonitorLogExporter(new AzureMonitorExporterOptions
        {
            ConnectionString = connectionString,
        });

        var processor = new BatchLogRecordExportProcessor(
            exporter,
            MaxQueueSize,
            ScheduledDelayMilliseconds,
            ExporterTimeoutMilliseconds,
            MaxExportBatchSize);
        _exportProcessor = processor;

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Trace)
                .AddOpenTelemetry(otel =>
                {
                    otel.IncludeScopes = true;
                    otel.AddProcessor(processor);
                });
        });

        _logger = _loggerFactory.CreateLogger("Pointframe.Telemetry");
    }

    internal TelemetryService(
        ILogger logger,
        IUserSettingsService userSettings,
        IAppVersionService appVersionService,
        ILogger<TelemetryService>? localLogger = null)
    {
        _userSettings = userSettings;
        _appVersion = appVersionService.Current.ToString();
        _logger = logger;
        _localLogger = localLogger ?? NullLogger<TelemetryService>.Instance;
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
        if (_disposed)
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

        if (_logger is null)
        {
            return;
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
        if (_disposed)
        {
            return;
        }

        var validation = TelemetryEventCatalog.Validate(name, properties);
        var channel = validation.Definition?.Channel ?? defaultChannel;
        if (!validation.IsValid)
        {
            LogSchemaValidationFailure(validation);
        }

        // Diagnostic events (heartbeat, startup) are background noise, not user actions.
        // Letting them overwrite the breadcrumb would make last_action useless on crash reports.
        if (channel == TelemetryChannel.Product)
        {
            _lastEventName = name;
        }

        if (_logger is null)
        {
            return;
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
        // Always report locally: source builds have no connection string, so the remote
        // logger is null and a schema mistake would otherwise go unnoticed until production.
        if (validation.IsKnownEvent)
        {
            var missingProperties = string.Join(",", validation.MissingProperties);
            _localLogger.LogWarning(
                "Telemetry schema mismatch for {EventName}. Missing required properties: {MissingProperties}",
                validation.EventName,
                missingProperties);
            _logger?.LogWarning(
                "Telemetry schema mismatch for {EventName}. Missing required properties: {MissingProperties}",
                validation.EventName,
                missingProperties);
            return;
        }

        _localLogger.LogWarning("Telemetry event {EventName} is not registered in TelemetryEventCatalog", validation.EventName);
        _logger?.LogWarning("Telemetry event {EventName} is not registered in TelemetryEventCatalog", validation.EventName);
    }

    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        _exportProcessor?.ForceFlush(FlushTimeoutMilliseconds);
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
