using System.Diagnostics.CodeAnalysis;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Pointframe.Automation;

namespace Pointframe.Services;

internal sealed class TelemetryService : ITelemetryService, IDisposable
{
    private const int FlushTimeoutMilliseconds = 5000;
    private const int MaxQueueSize = 2048;
    private const int ScheduledDelayMilliseconds = 5000;
    private const int ExporterTimeoutMilliseconds = 30000;
    private const int MaxExportBatchSize = 512;
    private const int MaxPropertyValueLength = 200;

    private readonly ILogger? _logger;
    private readonly ILogger<TelemetryService> _localLogger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly BaseProcessor<LogRecord>? _exportProcessor;
    private readonly IUserSettingsService _userSettings;
    private readonly string _appVersion;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly string _telemetrySchemaVersion = "1";
    private readonly object _syncRoot = new();
    private readonly bool _isAutomationMode;
    private volatile string? _lastEventName;
    private volatile bool _disposed;

    public TelemetryService(
        IConfiguration configuration,
        IUserSettingsService userSettings,
        IAppVersionService appVersionService,
        ILogger<TelemetryService> localLogger,
        AutomationLaunchOptions automationLaunchOptions)
    {
        _userSettings = userSettings;
        _appVersion = appVersionService.Current.ToString();
        _localLogger = localLogger;
        _isAutomationMode = automationLaunchOptions.IsAutomationMode;

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
        ILogger<TelemetryService>? localLogger = null,
        bool isAutomationMode = false)
    {
        _userSettings = userSettings;
        _appVersion = appVersionService.Current.ToString();
        _logger = logger;
        _localLogger = localLogger ?? NullLogger<TelemetryService>.Instance;
        _isAutomationMode = isAutomationMode;
    }

    public void TrackException(
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

        if (!IsRemoteEnabled)
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
        if (_disposed)
        {
            return;
        }

        var validation = TelemetryEventCatalog.Validate(name, properties);
        var channel = validation.Definition?.Channel ?? TelemetryChannel.Product;
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

        if (!IsRemoteEnabled)
        {
            return;
        }

        var scope = BuildScope(channel, properties);
        using (_logger.BeginScope(scope))
        {
            _logger.LogInformation("{microsoft.custom_event.name}", name);
        }
    }

    // The single authority on whether anything leaves the machine. Call sites never need to
    // ask: a missing connection string or an automation run silently drops every event here.
    [MemberNotNullWhen(true, nameof(_logger))]
    private bool IsRemoteEnabled => _logger is not null && !_isAutomationMode;

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
                scope[kvp.Key] = Clamp(kvp.Value);
            }
        }

        return scope;
    }

    // A dimension this long is always a bug (a path or recognised text that slipped through).
    // Truncating caps both the ingestion cost and the blast radius of that bug.
    private static string Clamp(string value)
    {
        return value.Length <= MaxPropertyValueLength ? value : value[..MaxPropertyValueLength];
    }

    private void LogSchemaValidationFailure(TelemetrySchemaValidationResult validation)
    {
        // Always report locally: source builds have no connection string, so the remote
        // logger is null and a schema mistake would otherwise go unnoticed until production.
        if (!validation.IsKnownEvent)
        {
            const string UnregisteredTemplate = "Telemetry event {EventName} is not registered in TelemetryEventCatalog";
            _localLogger.LogWarning(UnregisteredTemplate, validation.EventName);
            _logger?.LogWarning(UnregisteredTemplate, validation.EventName);
            return;
        }

        if (validation.MissingProperties.Count > 0)
        {
            LogWarningToBothSinks(
                "Telemetry schema mismatch for {EventName}. Missing required properties: {Properties}",
                validation.EventName,
                string.Join(",", validation.MissingProperties));
        }

        if (validation.UnknownProperties.Count > 0)
        {
            LogWarningToBothSinks(
                "Telemetry schema mismatch for {EventName}. Undeclared properties: {Properties}",
                validation.EventName,
                string.Join(",", validation.UnknownProperties));
        }
    }

    private void LogWarningToBothSinks(string template, string eventName, string properties)
    {
        _localLogger.LogWarning(template, eventName, properties);
        _logger?.LogWarning(template, eventName, properties);
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
