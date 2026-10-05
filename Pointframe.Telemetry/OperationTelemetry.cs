using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Pointframe.Telemetry;

internal sealed class OperationTelemetry : IOperationTelemetry, IDisposable
{
    internal const string EventName = "agent_operation";
    internal const string NameKey = "name";
    internal const string OutcomeKey = "outcome";
    internal const string DurationBucketKey = "duration_bucket";
    internal const string HostKey = "host";
    internal const string ClientKey = "client";
    internal const string VersionKey = "version";

    // Short in production so exiting never waits long for telemetry; the test assembly raises it because the first
    // emit builds the OpenTelemetry pipeline, which can take seconds on a loaded CI runner.
    internal static int FlushTimeoutMilliseconds { get; set; } = 2000;
    private const int MaxQueueSize = 512;
    private const int ScheduledDelayMilliseconds = 5000;
    private const int ExporterTimeoutMilliseconds = 10000;
    private const int MaxExportBatchSize = 128;

    private readonly TelemetryHost _host;
    private readonly string _version;
    private readonly Func<BaseExporter<LogRecord>> _exporterFactory;
    private readonly object _syncRoot = new();
    private Task _pending = Task.CompletedTask;
    private BaseProcessor<LogRecord>? _processor;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private bool _unavailable;
    private bool _disposed;

    internal OperationTelemetry(TelemetryHost host, string version, Func<BaseExporter<LogRecord>> exporterFactory)
    {
        _host = host;
        _version = version;
        _exporterFactory = exporterFactory;
    }

    // Emitting happens on a pool thread so a tool call or command never waits for the exporter's setup
    // (loading the Azure SDK takes noticeable time on a cold start) or for the network.
    public void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null)
    {
        try
        {
            var properties = BuildProperties(name, outcome, duration, mcpClientName);
            lock (_syncRoot)
            {
                if (_disposed || _unavailable)
                {
                    return;
                }

                _pending = _pending.ContinueWith(
                    _ => Emit(properties),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
        catch (Exception)
        {
            // Telemetry must never affect the operation it measures.
        }
    }

    public void Flush()
    {
        try
        {
            Task pending;
            lock (_syncRoot)
            {
                pending = _pending;
            }

            if (!pending.Wait(FlushTimeoutMilliseconds))
            {
                return;
            }

            _processor?.ForceFlush(FlushTimeoutMilliseconds);
        }
        catch (Exception)
        {
            // A failed flush loses the last events and nothing else.
        }
    }

    public void Dispose()
    {
        Flush();
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _processor?.Shutdown(FlushTimeoutMilliseconds);
            _loggerFactory?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown failures cannot be reported anywhere useful.
        }
    }

    internal Dictionary<string, object?> BuildProperties(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName)
    {
        var properties = new Dictionary<string, object?>
        {
            [NameKey] = TelemetryAllowlist.OperationName(_host, name),
            [OutcomeKey] = TelemetryAllowlist.OutcomeName(outcome),
            [DurationBucketKey] = TelemetryAllowlist.DurationBucket(duration),
            [HostKey] = TelemetryAllowlist.HostName(_host),
            [VersionKey] = _version,
        };

        if (_host == TelemetryHost.Mcp)
        {
            properties[ClientKey] = TelemetryAllowlist.McpClient(mcpClientName);
        }

        return properties;
    }

    private void Emit(Dictionary<string, object?> properties)
    {
        try
        {
            if (!EnsureLogger())
            {
                return;
            }

            using (_logger!.BeginScope(properties))
            {
                _logger.LogInformation("{microsoft.custom_event.name}", EventName);
            }
        }
        catch (Exception)
        {
            // Telemetry must never affect the operation it measures.
        }
    }

    private bool EnsureLogger()
    {
        if (_logger is not null)
        {
            return true;
        }

        try
        {
            var processor = new BatchLogRecordExportProcessor(
                new SafeExporter(_exporterFactory()),
                MaxQueueSize,
                ScheduledDelayMilliseconds,
                ExporterTimeoutMilliseconds,
                MaxExportBatchSize);
            _processor = processor;
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Information)
                    .AddOpenTelemetry(otel =>
                    {
                        otel.IncludeScopes = true;
                        // Only this fixed identity is exported: no host, process, OS, or OTEL_* resource
                        // attributes, so Azure cannot fill the role instance with the machine name.
                        otel.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService(
                            serviceName: _host == TelemetryHost.Cli ? "Pointframe.Cli" : "Pointframe.Mcp",
                            serviceInstanceId: TelemetryAllowlist.HostName(_host)));
                        otel.AddProcessor(processor);
                    });
            });
            _logger = _loggerFactory.CreateLogger("Pointframe.Telemetry");
            return true;
        }
        catch (Exception)
        {
            lock (_syncRoot)
            {
                _unavailable = true;
            }

            return false;
        }
    }

    internal static BaseExporter<LogRecord> CreateAzureExporter(string connectionString)
    {
        return new AzureMonitorLogExporter(new AzureMonitorExporterOptions
        {
            ConnectionString = connectionString,
            EnableLiveMetrics = false,
            // Offline storage would write events to disk and resend them on a later run, outliving
            // an opt-out and the process that measured them.
            DisableOfflineStorage = true,
        });
    }

    // The batch worker runs exporters on a dedicated thread and does not catch what they throw, so an
    // exporter fault would otherwise terminate the host process.
    private sealed class SafeExporter(BaseExporter<LogRecord> inner) : BaseExporter<LogRecord>
    {
        private static readonly System.Reflection.PropertyInfo? _parentProvider =
            typeof(BaseExporter<LogRecord>).GetProperty(nameof(ParentProvider));

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            try
            {
                // The Azure exporter reads the resource from its own provider, which only the wrapper has.
                if (inner.ParentProvider is null && ParentProvider is not null)
                {
                    _parentProvider?.SetMethod?.Invoke(inner, [ParentProvider]);
                }

                return inner.Export(batch);
            }
            catch (Exception)
            {
                return ExportResult.Failure;
            }
        }

        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
            try
            {
                return inner.ForceFlush(timeoutMilliseconds);
            }
            catch (Exception)
            {
                return false;
            }
        }

        protected override bool OnShutdown(int timeoutMilliseconds)
        {
            try
            {
                return inner.Shutdown(timeoutMilliseconds);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
