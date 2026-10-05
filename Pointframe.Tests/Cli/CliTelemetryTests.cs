using System.IO;
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
        Assert.Equal(("other", TelemetryOutcome.Error), (tracked.Name, tracked.Outcome));
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
