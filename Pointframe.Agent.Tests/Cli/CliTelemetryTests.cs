using System.IO;
using Moq;
using Pointframe.Engine;
using Pointframe.Cli;
using Pointframe.Telemetry;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class CliTelemetryTests
{
    [Fact]
    public async Task RunAsync_ForACommand_TracksItsNameOutcomeAndDuration()
    {
        var telemetry = new RecordingTelemetry();

        var exitCode = await CliApplication.RunAsync(["displays"], new StringWriter(), new StringWriter(), CancellationToken.None, telemetry);

        Assert.Equal(0, exitCode);
        var tracked = Assert.Single(telemetry.Tracked);
        Assert.Equal(("displays", TelemetryOutcome.Success), (tracked.Name, tracked.Outcome));
        Assert.Equal(1, telemetry.Flushes);
    }

    [Fact]
    public async Task RunAsync_ForUnparseableArguments_TracksOtherWithoutTheArguments()
    {
        var telemetry = new RecordingTelemetry();

        var exitCode = await CliApplication.RunAsync([@"C:\Users\someone\secret.png"], new StringWriter(), new StringWriter(), CancellationToken.None, telemetry);

        Assert.Equal(2, exitCode);
        var tracked = Assert.Single(telemetry.Tracked);
        Assert.Equal(("other", TelemetryOutcome.InvalidRequest), (tracked.Name, tracked.Outcome));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task RunAsync_ForHelpAndVersion_TracksNothing(string argument)
    {
        var telemetry = new RecordingTelemetry();

        var exitCode = await CliApplication.RunAsync([argument], new StringWriter(), new StringWriter(), CancellationToken.None, telemetry);

        Assert.Equal(0, exitCode);
        Assert.Empty(telemetry.Tracked);
    }

    [Fact]
    public async Task RunAsync_ForAnUnknownCommand_IsAnInvalidRequestWithTheSameExitCode()
    {
        var telemetry = new RecordingTelemetry();

        var exitCode = await CliApplication.RunAsync(["frobnicate"], new StringWriter(), new StringWriter(), CancellationToken.None, telemetry);

        Assert.Equal(2, exitCode);
        Assert.Equal(TelemetryOutcome.InvalidRequest, Assert.Single(telemetry.Tracked).Outcome);
    }

    [Fact]
    public async Task RunAsync_VerifyWithAnInvalidSpec_IsAnInvalidRequestWithTheSameExitCode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PointframeCliTelemetryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var spec = Path.Combine(directory, "verify.json");
            File.WriteAllText(spec, "{ \"schemaVersion\": 99 }");
            var telemetry = new RecordingTelemetry();

            var exitCode = await CliApplication.RunAsync(["verify", "run", "--spec", spec], new StringWriter(), new StringWriter(), CancellationToken.None, telemetry);

            Assert.Equal(2, exitCode);
            var tracked = Assert.Single(telemetry.Tracked);
            Assert.Equal(("verify", TelemetryOutcome.InvalidRequest), (tracked.Name, tracked.Outcome));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureWithARejectedOutputPath_IsAnInvalidRequestAndKeepsExitCodeOne()
    {
        var capture = new Mock<IDirectCaptureService>();
        capture
            .Setup(service => service.CaptureMonitorAsync(It.IsAny<string>(), It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("The output path is a directory.", "outputPath"));
        var application = new CliApplication(capture.Object, new Mock<IDirectRecordingService>().Object, new StringWriter(), new StringWriter());

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\.\DISPLAY1", "--output", @"C:\shots\"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(TelemetryOutcome.InvalidRequest, application.Outcome);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task CaptureThatFailsForAnyOtherReason_IsLeftAsAnErrorAndKeepsExitCodeOne(Type exceptionType)
    {
        var capture = new Mock<IDirectCaptureService>();
        capture
            .Setup(service => service.CaptureMonitorAsync(It.IsAny<string>(), It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "boom")!);
        var application = new CliApplication(capture.Object, new Mock<IDirectRecordingService>().Object, new StringWriter(), new StringWriter());

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\.\DISPLAY1"]);

        Assert.Equal(1, exitCode);
        Assert.Null(application.Outcome);
    }

    [Theory]
    [InlineData("invalid_frames_per_second", TelemetryOutcome.InvalidRequest)]
    [InlineData("invalid_output_path", TelemetryOutcome.InvalidRequest)]
    [InlineData("monitor_not_found", null)]
    [InlineData("recording_already_active", null)]
    public async Task RecordThatFailsToStart_IsClassifiedByItsTypedErrorCode(string code, TelemetryOutcome? expected)
    {
        var recording = new Mock<IDirectRecordingService>();
        recording.Setup(service => service.Start(It.IsAny<DirectRecordingRequest>())).Returns(new DirectRecordingResult(false, ErrorCode: code, ErrorMessage: "x"));
        var application = new CliApplication(new Mock<IDirectCaptureService>().Object, recording.Object, new StringWriter(), new StringWriter());

        var exitCode = await application.RunAsync(["record", "--monitor", @"\.\DISPLAY1", "--seconds", "1"]);

        Assert.Equal(1, exitCode);
        Assert.Equal(expected, application.Outcome);
    }

    [Fact]
    public void EveryOutcome_HasAnAllowlistedName()
    {
        var names = Enum.GetValues<TelemetryOutcome>().Select(TelemetryAllowlist.OutcomeName).ToArray();

        Assert.Equal(
            ["success", "error", "cancelled", "denied", "verification_failed", "invalid_request"],
            names);
    }

    [Fact]
    public async Task RunAsync_WhenTelemetryThrows_StillReturnsTheCommandResult()
    {
        var exitCode = await CliApplication.RunAsync(["displays"], new StringWriter(), new StringWriter(), CancellationToken.None, new ThrowingTelemetry());

        Assert.Equal(0, exitCode);
    }

    private sealed class RecordingTelemetry : IOperationTelemetry
    {
        public List<(string Name, TelemetryOutcome Outcome)> Tracked { get; } = [];

        public int Flushes { get; private set; }

        public void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null) => Tracked.Add((name, outcome));

        public void Flush() => Flushes++;
    }

    private sealed class ThrowingTelemetry : IOperationTelemetry
    {
        public void Track(string name, TelemetryOutcome outcome, TimeSpan duration, string? mcpClientName = null) => throw new InvalidOperationException("telemetry failure");

        public void Flush()
        {
        }
    }
}
