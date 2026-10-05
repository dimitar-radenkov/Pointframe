using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Pointframe.Automation;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TelemetryServiceTests
{
    private sealed class LogEntry
    {
        public LogLevel Level { get; init; }
        public string Message { get; init; } = string.Empty;
        public Exception? Exception { get; init; }
        public Dictionary<string, object?> Scope { get; init; } = [];
    }

    private sealed class CapturingLogger : ILogger<TelemetryService>
    {
        private readonly List<LogEntry> _entries = [];
        private readonly Dictionary<string, object?> _currentScope = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var added = new List<string>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var kvp in pairs)
                {
                    _currentScope[kvp.Key] = kvp.Value;
                    added.Add(kvp.Key);
                }
            }

            return new ScopeHandle(() =>
            {
                foreach (var key in added)
                {
                    _currentScope.Remove(key);
                }
            });
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry
            {
                Level = logLevel,
                Message = formatter(state, exception),
                Exception = exception,
                Scope = new Dictionary<string, object?>(_currentScope),
            });
        }

        private sealed class ScopeHandle(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private static IUserSettingsService SettingsWithInstallId(string? installId)
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings { InstallId = installId });
        return mock.Object;
    }

    private static IAppVersionService AppVersion(Version? version = null)
        => Mock.Of<IAppVersionService>(s => s.Current == (version ?? new Version(1, 2, 3)));

    private static TelemetryService CreateSut(CapturingLogger logger, string? installId = "install-abc")
        => new(logger, SettingsWithInstallId(installId), AppVersion());

    private static TelemetryService CreateDisabledSut(CapturingLogger localLogger)
    {
        var config = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        config.Setup(c => c["ApplicationInsights:ConnectionString"]).Returns((string?)null);
        return new TelemetryService(
            config.Object,
            SettingsWithInstallId("id"),
            AppVersion(),
            localLogger,
            AutomationLaunchOptions.Parse([]));
    }

    private static TelemetryService CreateAutomationSut(CapturingLogger logger)
        => new(logger, SettingsWithInstallId("install-abc"), AppVersion(), localLogger: null, isAutomationMode: true);

    [Fact]
    public void TrackEvent_WhenConnectionStringMissing_DoesNotThrow()
    {
        // Arrange
        var sut = CreateDisabledSut(new CapturingLogger());

        // Act
        var ex = Record.Exception(() => sut.TrackEvent("some_event"));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void TrackException_WhenConnectionStringMissing_DoesNotThrow()
    {
        // Arrange
        var sut = CreateDisabledSut(new CapturingLogger());

        // Act
        var ex = Record.Exception(() => sut.TrackException(new InvalidOperationException("oops")));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void TrackEvent_WhenTelemetryDisabled_WarnsLocallyAboutUnregisteredEvent()
    {
        // Arrange
        var localLogger = new CapturingLogger();
        var sut = CreateDisabledSut(localLogger);

        // Act
        sut.TrackEvent("not_registered_event");

        // Assert
        Assert.Contains(localLogger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("not_registered_event", StringComparison.Ordinal));
    }

    [Fact]
    public void TrackEvent_WhenTelemetryDisabled_WarnsLocallyAboutMissingRequiredProperties()
    {
        // Arrange
        var localLogger = new CapturingLogger();
        var sut = CreateDisabledSut(localLogger);

        // Act
        sut.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
        });

        // Assert
        Assert.Contains(localLogger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(TelemetryPropertyKeys.Source, StringComparison.Ordinal));
    }

    [Fact]
    public void TrackEvent_WhenTelemetryDisabledAndEventIsValid_LogsNothingLocally()
    {
        // Arrange
        var localLogger = new CapturingLogger();
        var sut = CreateDisabledSut(localLogger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Empty(localLogger.Entries);
    }

    [Fact]
    public void TrackEvent_LogsOneEntry()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void TrackEvent_KnownProductEvent_IncludesProductChannelInScope()
    {
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        sut.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
            [TelemetryPropertyKeys.Source] = "hotkey",
        });

        Assert.Equal("product", logger.Entries[0].Scope["telemetry_channel"]);
    }

    [Fact]
    public void TrackEvent_KnownDiagnosticEvent_IncludesDiagnosticChannelInScope()
    {
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        sut.TrackEvent(TelemetryEvents.AppHeartbeat, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.UptimeMinutes] = "30",
        });

        Assert.Equal("diagnostic", logger.Entries[0].Scope["telemetry_channel"]);
    }

    [Fact]
    public void TrackEvent_WhenRequiredPropertiesMissing_LogsSchemaWarning()
    {
        var logger = new CapturingLogger();
        var localLogger = new CapturingLogger();
        var sut = new TelemetryService(logger, SettingsWithInstallId("install-abc"), AppVersion(), localLogger);

        sut.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
        });

        Assert.Contains(localLogger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void TrackEvent_MessageContainsEventName()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Contains(TelemetryEvents.CapturePinned, logger.Entries[0].Message);
    }

    [Fact]
    public void TrackEvent_LogsAtInformationLevel()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent("capture_pinned");

        // Assert
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
    }

    [Fact]
    public void TrackEvent_IncludesInstallIdInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger, installId: "abc123");

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Equal("abc123", logger.Entries[0].Scope["install_id"]);
    }

    [Fact]
    public void TrackEvent_IncludesVersionInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = new TelemetryService(logger, SettingsWithInstallId("abc123"), AppVersion(new Version(9, 8, 7)));

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Equal("9.8.7", logger.Entries[0].Scope["version"]);
    }

    [Fact]
    public void TrackEvent_OmitsInstallIdWhenNull()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger, installId: null);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.DoesNotContain("install_id", logger.Entries[0].Scope.Keys);
    }

    [Fact]
    public void TrackEvent_OmitsInstallIdWhenEmpty()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger, installId: string.Empty);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.DoesNotContain("install_id", logger.Entries[0].Scope.Keys);
    }

    [Fact]
    public void TrackEvent_IncludesAdditionalPropertiesInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
            [TelemetryPropertyKeys.Source] = "tray",
        });

        // Assert
        Assert.Equal("region", logger.Entries[0].Scope["type"]);
    }

    [Fact]
    public void TrackEvent_AdditionalPropertiesCoexistWithInstallId()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger, installId: "xyz");

        // Act
        sut.TrackEvent(TelemetryEvents.RecordingStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "whole_screen",
        });

        // Assert
        var scope = logger.Entries[0].Scope;
        Assert.Equal("xyz", scope["install_id"]);
        Assert.Equal("whole_screen", scope["type"]);
    }

    [Fact]
    public void TrackException_LogsOneEntry()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new InvalidOperationException("boom"));

        // Assert
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void TrackException_LogsDiagnosticChannelInScope()
    {
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        sut.TrackException(new InvalidOperationException("boom"), "dispatcher");

        Assert.Equal("diagnostic", logger.Entries[0].Scope["telemetry_channel"]);
    }

    [Fact]
    public void TrackException_LogsAtErrorLevel()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new ArgumentException("bad"));

        // Assert
        Assert.Equal(LogLevel.Error, logger.Entries[0].Level);
    }

    [Fact]
    public void TrackException_DoesNotForwardExceptionObjectToRemoteLogger()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new InvalidOperationException("local-only details"));

        // Assert
        Assert.Null(logger.Entries[0].Exception);
    }

    [Fact]
    public void TrackException_IncludesExceptionTypeInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new InvalidOperationException("oops"));

        // Assert
        Assert.Equal("InvalidOperationException", logger.Entries[0].Scope["exception_type"]);
    }

    [Fact]
    public void TrackException_IncludesContextWhenProvided()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new Exception("x"), context: "gif_export");

        // Assert
        Assert.Equal("gif_export", logger.Entries[0].Scope["context"]);
    }

    [Fact]
    public void TrackException_OmitsContextWhenNull()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new Exception("x"), context: null);

        // Assert
        Assert.DoesNotContain("context", logger.Entries[0].Scope.Keys);
    }

    [Fact]
    public void TrackException_IncludesInstallIdInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger, installId: "install-xyz");

        // Act
        sut.TrackException(new Exception("x"));

        // Assert
        Assert.Equal("install-xyz", logger.Entries[0].Scope["install_id"]);
    }

    [Fact]
    public void TrackException_IncludesVersionInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = new TelemetryService(logger, SettingsWithInstallId("install-xyz"), AppVersion(new Version(9, 8, 7)));

        // Act
        sut.TrackException(new Exception("x"));

        // Assert
        Assert.Equal("9.8.7", logger.Entries[0].Scope["version"]);
    }

    [Fact]
    public void TrackEvent_IncludesSessionIdInScope()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.True(logger.Entries[0].Scope.ContainsKey("session_id"));
        Assert.NotNull(logger.Entries[0].Scope["session_id"]);
    }

    [Fact]
    public void TrackEvent_SessionIdIsConsistentAcrossEvents()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        var first = logger.Entries[0].Scope["session_id"];
        var second = logger.Entries[1].Scope["session_id"];
        Assert.Equal(first, second);
    }

    [Fact]
    public void TrackEvent_SessionIdDiffersAcrossInstances()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut1 = CreateSut(logger);
        var sut2 = CreateSut(logger);

        // Act
        sut1.TrackEvent(TelemetryEvents.CapturePinned);
        sut2.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.NotEqual(logger.Entries[0].Scope["session_id"], logger.Entries[1].Scope["session_id"]);
    }

    [Fact]
    public void TrackException_IncludesLastActionWhenEventWasPreviouslyTracked()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Act
        sut.TrackException(new InvalidOperationException("boom"));

        // Assert
        Assert.Equal(TelemetryEvents.CapturePinned, logger.Entries[1].Scope[TelemetryPropertyKeys.LastAction]);
    }

    [Fact]
    public void TrackException_OmitsLastActionWhenNoEventWasPreviouslyTracked()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackException(new InvalidOperationException("boom"));

        // Assert
        Assert.DoesNotContain("last_action", logger.Entries[0].Scope.Keys);
    }

    [Fact]
    public void TrackException_LastActionIgnoresDiagnosticEvents()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);
        sut.TrackEvent(TelemetryEvents.CapturePinned);
        sut.TrackEvent(TelemetryEvents.AppHeartbeat, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.UptimeMinutes] = "240",
        });

        // Act
        sut.TrackException(new InvalidOperationException("boom"));

        // Assert
        Assert.Equal(TelemetryEvents.CapturePinned, logger.Entries[2].Scope[TelemetryPropertyKeys.LastAction]);
    }

    [Fact]
    public void TrackEvent_WhenPropertyIsNotDeclaredInCatalog_LogsSchemaWarning()
    {
        // Arrange
        var logger = new CapturingLogger();
        var localLogger = new CapturingLogger();
        var sut = new TelemetryService(logger, SettingsWithInstallId("install-abc"), AppVersion(), localLogger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned, new Dictionary<string, string>
        {
            ["file_path"] = @"C:\captures\holiday-photo.png",
        });

        // Assert
        Assert.Contains(localLogger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("file_path", StringComparison.Ordinal));
        Assert.DoesNotContain(localLogger.Entries, entry => entry.Message.Contains(@"C:\captures\holiday-photo.png", StringComparison.Ordinal));
    }

    [Fact]
    public void TrackEvent_WhenPropertyIsNotDeclaredInCatalog_DropsPropertyBeforeExport()
    {
        var logger = new CapturingLogger();
        var localLogger = new CapturingLogger();
        var sut = new TelemetryService(logger, SettingsWithInstallId("install-abc"), AppVersion(), localLogger);

        sut.TrackEvent(TelemetryEvents.CapturePinned, new Dictionary<string, string>
        {
            ["file_path"] = @"C:\captures\holiday-photo.png",
        });

        Assert.DoesNotContain("file_path", logger.Entries.Single(entry => entry.Level == LogLevel.Information).Scope.Keys);
        Assert.Contains(localLogger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExporterPayload_UsesMinimalResourceAndIgnoresHostileEnvironmentOverrides()
    {
        var previousEnvironment = new Dictionary<string, string?>
        {
            ["OTEL_RESOURCE_ATTRIBUTES"] = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"),
            ["OTEL_SERVICE_NAME"] = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME"),
            ["COMPUTERNAME"] = Environment.GetEnvironmentVariable("COMPUTERNAME"),
            ["USERNAME"] = Environment.GetEnvironmentVariable("USERNAME"),
            ["USERDOMAIN"] = Environment.GetEnvironmentVariable("USERDOMAIN"),
        };
        const string hostileHost = "HOST-OVERRIDE-PRIVACY-CHECK";
        const string hostileUser = "USER-OVERRIDE-PRIVACY-CHECK";
        const string hostileDomain = "DOMAIN-OVERRIDE-PRIVACY-CHECK";
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        try
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", $"host.name={hostileHost},process.user.name={hostileUser},host.id={hostileDomain}");
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "HostileServiceName");
            Environment.SetEnvironmentVariable("COMPUTERNAME", hostileHost);
            Environment.SetEnvironmentVariable("USERNAME", hostileUser);
            Environment.SetEnvironmentVariable("USERDOMAIN", hostileDomain);

            var configuration = new Mock<IConfiguration>();
            configuration.SetupGet(config => config["ApplicationInsights:ConnectionString"])
                .Returns($"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:{port}/");
            using var sut = new TelemetryService(
                configuration.Object,
                SettingsWithInstallId("anonymous-install-id"),
                AppVersion(),
                Mock.Of<ILogger<TelemetryService>>(),
                AutomationLaunchOptions.Parse([]));

            var payloadTask = CaptureRequestPayloadAsync(listener);
            sut.TrackEvent(TelemetryEvents.AppStarted, new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.OsBuild] = "10.0.22631",
                [TelemetryPropertyKeys.ScreenCount] = "2",
                ["file_path"] = Environment.CurrentDirectory,
            });
            sut.Flush();

            var payload = await payloadTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("Pointframe", payload, StringComparison.Ordinal);
            Assert.Contains("desktop", payload, StringComparison.Ordinal);
            Assert.Contains("ai.cloud.role", payload, StringComparison.Ordinal);
            Assert.Contains("ai.cloud.roleInstance", payload, StringComparison.Ordinal);
            Assert.Contains("app_started", payload, StringComparison.Ordinal);
            Assert.Contains("anonymous-install-id", payload, StringComparison.Ordinal);
            Assert.Contains("telemetry_schema_version", payload, StringComparison.Ordinal);
            Assert.Contains("\"2\"", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("file_path", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("ai.user.id", payload, StringComparison.Ordinal);

            var forbiddenValues = new[]
            {
                Environment.MachineName,
                Environment.UserName,
                Environment.UserDomainName,
                Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppContext.BaseDirectory,
                hostileHost,
                hostileUser,
                hostileDomain,
                "HostileServiceName",
            };
            foreach (var forbiddenValue in forbiddenValues.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var serializedValue = JsonSerializer.Serialize(forbiddenValue)[1..^1];
                Assert.DoesNotContain(serializedValue, payload, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            foreach (var (name, value) in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static async Task<string> CaptureRequestPayloadAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        await using var body = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(body);
        var bytes = body.ToArray();
        if (context.Request.Headers["Content-Encoding"]?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true)
        {
            await using var compressed = new MemoryStream(bytes);
            await using var decompressor = new GZipStream(compressed, CompressionMode.Decompress);
            await using var decompressed = new MemoryStream();
            await decompressor.CopyToAsync(decompressed);
            bytes = decompressed.ToArray();
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.Close();
        return Encoding.UTF8.GetString(bytes);
    }

    [Fact]
    public void TrackEvent_ClampsOverlongPropertyValues()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.AboutUrlOpened, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.UrlHost] = new string('x', 5000),
        });

        // Assert
        var value = Assert.IsType<string>(logger.Entries[0].Scope[TelemetryPropertyKeys.UrlHost]);
        Assert.Equal(200, value.Length);
    }

    [Fact]
    public void TrackEvent_LeavesShortPropertyValuesIntact()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.AboutUrlOpened, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.UrlHost] = "github.com",
        });

        // Assert
        Assert.Equal("github.com", logger.Entries[0].Scope[TelemetryPropertyKeys.UrlHost]);
    }

    [Fact]
    public void TrackEvent_InAutomationMode_EmitsNothing()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateAutomationSut(logger);

        // Act
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TrackException_InAutomationMode_EmitsNothing()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateAutomationSut(logger);

        // Act
        sut.TrackException(new InvalidOperationException("boom"));

        // Assert
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Flush_DoesNotStopSubsequentTracking()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.Flush();
        sut.TrackEvent(TelemetryEvents.CapturePinned);

        // Assert
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Flush_CanBeCalledAfterDispose()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);
        sut.Dispose();

        // Act
        var ex = Record.Exception(sut.Flush);

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);
        sut.Dispose();

        // Act
        var ex = Record.Exception(() => sut.Dispose());

        // Assert
        Assert.Null(ex);
    }
}
