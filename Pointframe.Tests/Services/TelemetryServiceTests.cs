using Microsoft.Extensions.Logging;
using Moq;
using Pointframe.Models;
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

    private sealed class CapturingLogger : ILogger
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

    [Fact]
    public void TrackEvent_WhenConnectionStringMissing_DoesNotThrow()
    {
        // Arrange
        var config = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        config.Setup(c => c["ApplicationInsights:ConnectionString"]).Returns((string?)null);
        var sut = new TelemetryService(config.Object, SettingsWithInstallId("id"), AppVersion());

        // Act
        var ex = Record.Exception(() => sut.TrackEvent("some_event"));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void TrackException_WhenConnectionStringMissing_DoesNotThrow()
    {
        // Arrange
        var config = new Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        config.Setup(c => c["ApplicationInsights:ConnectionString"]).Returns((string?)null);
        var sut = new TelemetryService(config.Object, SettingsWithInstallId("id"), AppVersion());

        // Act
        var ex = Record.Exception(() => sut.TrackException(new InvalidOperationException("oops")));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void TrackEvent_LogsOneEntry()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent("snip_started");

        // Assert
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void TrackEvent_MessageContainsEventName()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);

        // Act
        sut.TrackEvent("snip_started");

        // Assert
        Assert.Contains("snip_started", logger.Entries[0].Message);
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
        sut.TrackEvent("annotation_committed");

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
        sut.TrackEvent("annotation_committed");

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
        sut.TrackEvent("annotation_committed");

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
        sut.TrackEvent("annotation_committed");

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
        sut.TrackEvent("snip_started", new Dictionary<string, string> { ["type"] = "region" });

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
        sut.TrackEvent("recording_started", new Dictionary<string, string> { ["type"] = "whole_screen" });

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
        sut.TrackEvent("snip_started");

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
        sut.TrackEvent("snip_started");
        sut.TrackEvent("capture_completed");

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
        sut1.TrackEvent("snip_started");
        sut2.TrackEvent("snip_started");

        // Assert
        Assert.NotEqual(logger.Entries[0].Scope["session_id"], logger.Entries[1].Scope["session_id"]);
    }

    [Fact]
    public void TrackException_IncludesLastActionWhenEventWasPreviouslyTracked()
    {
        // Arrange
        var logger = new CapturingLogger();
        var sut = CreateSut(logger);
        sut.TrackEvent("annotation_committed");

        // Act
        sut.TrackException(new InvalidOperationException("boom"));

        // Assert
        Assert.Equal("annotation_committed", logger.Entries[1].Scope["last_action"]);
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
