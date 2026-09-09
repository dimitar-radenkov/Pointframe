using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class CliApplicationTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void TryParse_HelpFlag_UsesHelpCommand(string flag)
    {
        var parsed = CliCommandParser.TryParse([flag], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("help", command.Name);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    [InlineData("version")]
    public void TryParse_VersionFlag_UsesVersionCommand(string flag)
    {
        var parsed = CliCommandParser.TryParse([flag], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("version", command.Name);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void TryParse_HelpFlagAmongOtherArguments_TakesPriorityAndUsesHelpCommand(string flag)
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1", flag], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("help", command.Name);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void TryParse_VersionFlagAmongOtherArguments_TakesPriorityAndUsesVersionCommand(string flag)
    {
        var parsed = CliCommandParser.TryParse(["capture", flag], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("version", command.Name);
    }

    [Fact]
    public async Task RunAsync_Help_WritesHelpTextAndExitsZero()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Pointframe CLI", standardOutput.ToString());
        Assert.Contains("displays", standardOutput.ToString());
        Assert.Empty(standardError.ToString());
        directCaptureService.VerifyNoOtherCalls();
        directRecordingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_Version_WritesVersionAndExitsZero()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["--version"]);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("Pointframe CLI ", standardOutput.ToString());
        Assert.Empty(standardError.ToString());
        directCaptureService.VerifyNoOtherCalls();
        directRecordingService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_Displays_WritesDisplayDescriptorsJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.ListDisplays())
            .Returns("[{\"monitorName\":\"\\\\\\\\.\\\\DISPLAY1\"}]");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["displays"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.ListDisplays(), Times.Once);
        using var output = JsonDocument.Parse(standardOutput.ToString());
        Assert.Equal(@"\\.\DISPLAY1", output.RootElement[0].GetProperty("monitorName").GetString());
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public void TryParse_Displays_UsesDisplaysListCommand()
    {
        var parsed = CliCommandParser.TryParse(["displays"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("displays", command.Name);
        Assert.Null(command.MonitorName);
    }

    [Fact]
    public void TryParse_Capture_RequiresExactMonitorArgument()
    {
        var parsed = CliCommandParser.TryParse(["capture", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
    }

    [Fact]
    public void TryParse_Capture_AcceptsShortMonitorAlias()
    {
        var parsed = CliCommandParser.TryParse(["capture", "-m", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
    }

    [Fact]
    public void TryParse_CaptureWithoutMonitor_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["capture"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The capture command requires --monitor followed by an exact Windows device name.", error);
    }

    [Fact]
    public void TryParse_CaptureWithRegion_ParsesRegionRelativeToMonitor()
    {
        var parsed = CliCommandParser.TryParse(
            ["capture", "--monitor", @"\\.\DISPLAY1", "--region", "10,20,300,400"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
        Assert.Equal(new CaptureRegion(10, 20, 300, 400), command.Region);
    }

    [Fact]
    public void TryParse_CaptureWithRegion_AcceptsShortAliasAndFlagOrderIndependence()
    {
        var parsed = CliCommandParser.TryParse(
            ["capture", "-g", "10,20,300,400", "-m", @"\\.\DISPLAY1"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
        Assert.Equal(new CaptureRegion(10, 20, 300, 400), command.Region);
    }

    [Fact]
    public void TryParse_CaptureWithoutRegion_LeavesRegionNull()
    {
        var parsed = CliCommandParser.TryParse(["capture", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Null(command.Region);
    }

    [Theory]
    [InlineData("10,20,300")]
    [InlineData("10,20,300,0")]
    [InlineData("10,20,0,400")]
    [InlineData("not,a,region,value")]
    public void TryParse_CaptureWithInvalidRegion_ReturnsUsageError(string regionValue)
    {
        var parsed = CliCommandParser.TryParse(
            ["capture", "--monitor", @"\\.\DISPLAY1", "--region", regionValue],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal(
            "Each --region value must be four comma-separated integers formatted as x,y,width,height with a positive width and height.",
            error);
    }

    [Fact]
    public void TryParse_OcrWithRegion_ParsesRegion()
    {
        var parsed = CliCommandParser.TryParse(
            ["ocr", "--monitor", @"\\.\DISPLAY1", "--region", "1,2,3,4"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(new CaptureRegion(1, 2, 3, 4), command.Region);
    }

    [Fact]
    public void TryParse_Ocr_RequiresExactMonitorArgument()
    {
        var parsed = CliCommandParser.TryParse(["ocr", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("ocr", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
    }

    [Fact]
    public void TryParse_OcrWithoutMonitor_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["ocr"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The ocr command requires --monitor followed by an exact Windows device name.", error);
    }

    [Fact]
    public void TryParse_Record_ParsesMonitorSecondsFpsAndRedactionRegions()
    {
        var parsed = CliCommandParser.TryParse(
            ["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5", "--fps", "30", "--redact", "10,20,30,40", "--redact", "1,2,3,4"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("record", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
        Assert.Equal(5, command.RecordSeconds);
        Assert.Equal(30, command.FramesPerSecond);
        Assert.NotNull(command.RedactionRegions);
        Assert.Equal(2, command.RedactionRegions!.Count);
        Assert.Equal(new PixelBounds(10, 20, 30, 40), command.RedactionRegions[0]);
        Assert.Equal(new PixelBounds(1, 2, 3, 4), command.RedactionRegions[1]);
    }

    [Fact]
    public void TryParse_Record_AcceptsShortOptionAliases()
    {
        var parsed = CliCommandParser.TryParse(
            ["record", "-m", @"\\.\DISPLAY1", "-s", "5", "-f", "30", "-r", "10,20,30,40"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("record", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
        Assert.Equal(5, command.RecordSeconds);
        Assert.Equal(30, command.FramesPerSecond);
        Assert.NotNull(command.RedactionRegions);
        Assert.Equal(new PixelBounds(10, 20, 30, 40), command.RedactionRegions![0]);
    }

    [Fact]
    public void TryParse_Record_AcceptsInlineFlagValues()
    {
        var parsed = CliCommandParser.TryParse(
            ["record", @"--monitor=\\.\DISPLAY1", "--seconds=5", "--fps=30", "--redact=10,20,30,40"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("record", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
        Assert.Equal(5, command.RecordSeconds);
        Assert.Equal(30, command.FramesPerSecond);
        Assert.NotNull(command.RedactionRegions);
        Assert.Equal(new PixelBounds(10, 20, 30, 40), command.RedactionRegions![0]);
    }

    [Fact]
    public void TryParse_Capture_AcceptsInlineMonitorValue()
    {
        var parsed = CliCommandParser.TryParse(["capture", @"--monitor=\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture", command.Name);
        Assert.Equal(@"\\.\DISPLAY1", command.MonitorName);
    }

    [Fact]
    public void TryParse_Record_DefaultsFramesPerSecondAndRedactionRegions()
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(20, command.FramesPerSecond);
        Assert.NotNull(command.RedactionRegions);
        Assert.Empty(command.RedactionRegions!);
    }

    [Fact]
    public void TryParse_RecordWithoutMonitor_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["record", "--seconds", "5"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --monitor followed by an exact Windows device name.", error);
    }

    [Fact]
    public void TryParse_RecordWithoutSeconds_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --seconds followed by a positive integer duration.", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void TryParse_RecordWithInvalidSeconds_ReturnsUsageError(string secondsValue)
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", secondsValue], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --seconds to be a positive integer.", error);
    }

    [Fact]
    public void TryParse_RecordWithInvalidRedactionRegion_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(
            ["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5", "--redact", "10,20,30"],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("Each --redact value must be four comma-separated integers formatted as x,y,width,height with a positive width and height.", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void TryParse_RecordWithInvalidFps_ReturnsUsageError(string fpsValue)
    {
        var parsed = CliCommandParser.TryParse(
            ["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5", "--fps", fpsValue],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --fps to be an integer between 1 and 60.", error);
    }

    [Fact]
    public void TryParse_RecordWithTrailingMonitorFlagAndNoValue_ReturnsMonitorUsageErrorNotUnrecognizedOption()
    {
        var parsed = CliCommandParser.TryParse(["record", "--seconds", "5", "--monitor"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --monitor followed by an exact Windows device name.", error);
    }

    [Fact]
    public void TryParse_RecordWithTrailingSecondsFlagAndNoValue_ReturnsSecondsUsageErrorNotUnrecognizedOption()
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1", "--seconds"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --seconds to be a positive integer.", error);
    }

    [Fact]
    public void TryParse_RecordWithTrailingFpsFlagAndNoValue_ReturnsFpsUsageErrorNotUnrecognizedOption()
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5", "--fps"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The record command requires --fps to be an integer between 1 and 60.", error);
    }

    [Fact]
    public void TryParse_RecordWithTrailingRedactFlagAndNoValue_ReturnsRedactUsageErrorNotUnrecognizedOption()
    {
        var parsed = CliCommandParser.TryParse(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5", "--redact"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("Each --redact value must be four comma-separated integers formatted as x,y,width,height with a positive width and height.", error);
    }

    [Fact]
    public async Task RunAsync_Capture_WritesDirectArtifactJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"artifact\":{\"metadata\":{\"artifactId\":\"artifact-1\"}}}");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("artifact-1", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_CaptureWithRegion_PassesParsedRegionToDirectCaptureService()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", new CaptureRegion(10, 20, 300, 400), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"artifact\":{\"metadata\":{\"artifactId\":\"artifact-1\"}}}");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1", "--region", "10,20,300,400"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(
            service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", new CaptureRegion(10, 20, 300, 400), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_Ocr_WritesRecognizedTextJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorTextAsync(@"\\.\DISPLAY1", It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"Artifact\":{\"Metadata\":{\"ArtifactId\":\"artifact-1\"}},\"RecognizedText\":\"Hello world\"}");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["ocr", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.CaptureMonitorTextAsync(@"\\.\DISPLAY1", It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Hello world", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_Record_StartsWaitsAndStopsThenWritesCombinedJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        var directRecordingService = new Mock<IDirectRecordingService>();
        var session = new DirectRecordingSession(
            1,
            "rec_1",
            @"C:\recordings\rec_1.mp4",
            @"\\.\DISPLAY1",
            20,
            new PixelBounds(0, 0, 1920, 1080),
            [],
            DateTimeOffset.UtcNow);
        var artifact = new DirectRecordingArtifact(
            1,
            "rec_1",
            "video/mp4",
            @"C:\recordings\rec_1.mp4",
            "deadbeef",
            1024,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            false,
            @"\\.\DISPLAY1",
            1.0,
            1.0,
            new PixelBounds(0, 0, 1920, 1080),
            new PixelBounds(0, 0, 1920, 1080),
            new PixelBounds(0, 0, 1920, 1080),
            @"C:\recordings\rec_1.mp4.events.jsonl",
            2,
            1);
        directRecordingService
            .Setup(service => service.Start(It.IsAny<DirectRecordingRequest>()))
            .Returns(new DirectRecordingResult(true, Session: session));
        directRecordingService
            .Setup(service => service.StopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectRecordingResult(true, Artifact: artifact));
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "1"]);

        Assert.Equal(0, exitCode);
        directRecordingService.Verify(
            service => service.Start(It.Is<DirectRecordingRequest>(request =>
                request.MonitorName == @"\\.\DISPLAY1" && request.FramesPerSecond == 20 && request.RedactionRegionsCaptureLocalPixels.Count == 0)),
            Times.Once);
        directRecordingService.Verify(service => service.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
        var response = JsonSerializer.Deserialize<DirectRecordingResponse>(standardOutput.ToString());
        Assert.NotNull(response);
        Assert.True(response!.Success);
        Assert.Equal("rec_1", response.Session?.OperationId);
        Assert.Equal("rec_1", response.Artifact?.ArtifactId);
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_Record_WhenStartFails_WritesFailureJsonAndDoesNotStop()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        var directRecordingService = new Mock<IDirectRecordingService>();
        directRecordingService
            .Setup(service => service.Start(It.IsAny<DirectRecordingRequest>()))
            .Returns(new DirectRecordingResult(false, ErrorCode: "monitor_not_found", ErrorMessage: "The monitor was not found."));
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "1"]);

        Assert.Equal(1, exitCode);
        directRecordingService.Verify(service => service.StopAsync(It.IsAny<CancellationToken>()), Times.Never);
        var response = JsonSerializer.Deserialize<DirectRecordingResponse>(standardOutput.ToString());
        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Equal("monitor_not_found", response.Error?.Code);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), "target_not_found")]
    [InlineData(typeof(InvalidOperationException), "target_not_capturable")]
    [InlineData(typeof(ArgumentOutOfRangeException), "invalid_region")]
    [InlineData(typeof(IOException), "capture_failed")]
    public async Task RunAsync_CaptureFailure_WritesFailureJsonWithMachineReadableCode(Type exceptionType, string expectedCode)
    {
        // The help text promises a single-line JSON response on standard output for every command
        // other than --help/--version. Before this, only 'record' honored that on failure: the capture
        // path wrote a bare "Pointframe CLI failed: ..." line to standard error and left stdout empty,
        // so a script parsing stdout had to scrape prose from stderr to learn why a command failed.
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(It.IsAny<string>(), It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "Capture went wrong.")!);
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1"]);

        Assert.Equal(1, exitCode);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(standardOutput.ToString());
        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.Equal(expectedCode, response.Error?.Code);
        Assert.Contains("Capture went wrong.", response.Error?.Message);

        // The human-readable line stays on standard error so interactive use is unchanged.
        Assert.Contains("Pointframe CLI failed:", standardError.ToString());
    }

    [Fact]
    public async Task StaticRunAsync_RuntimeFailure_WritesFailureJsonToStandardOutput()
    {
        // The static entry point constructs the real services, so a construction or execution failure
        // there must produce the same JSON contract as the instance path rather than stderr-only prose.
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        // A window id that cannot resolve is rejected by the engine as a runtime (not usage) error.
        var exitCode = await CliApplication.RunAsync(["capture-window", "--window-id", "1"], standardOutput, standardError);

        Assert.Equal(1, exitCode);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(standardOutput.ToString());
        Assert.NotNull(response);
        Assert.False(response!.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.Error?.Code));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("ocr")]
    public void TryParse_CaptureLikeWithOutput_ParsesExplicitFilePath(string commandName)
    {
        var parsed = CliCommandParser.TryParse(
            [commandName, "--monitor", @"\\.\DISPLAY1", "--output", @"C:\shots\shot.png"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(@"C:\shots\shot.png", command.OutputPath);
    }

    [Theory]
    [InlineData("capture-window")]
    [InlineData("ocr-window")]
    public void TryParse_WindowCaptureWithOutput_ParsesShortAlias(string commandName)
    {
        var parsed = CliCommandParser.TryParse(
            [commandName, "--window-id", "12345", "-o", "shot.png"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("shot.png", command.OutputPath);
    }

    [Fact]
    public void TryParse_RecordWithOutput_ParsesInlineValue()
    {
        var parsed = CliCommandParser.TryParse(
            ["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "5", @"--output=C:\clips\take1.mp4"],
            out var command,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(@"C:\clips\take1.mp4", command.OutputPath);
    }

    [Fact]
    public void TryParse_OutputWithoutValue_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["capture", "--monitor", @"\\.\DISPLAY1", "--output"], out _, out var error);

        Assert.False(parsed);
        Assert.Equal("The capture command requires --output followed by a file path.", error);
    }

    [Fact]
    public void TryParse_WithoutOutput_LeavesOutputPathNull()
    {
        var parsed = CliCommandParser.TryParse(["capture", "--monitor", @"\\.\DISPLAY1"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Null(command.OutputPath);
    }

    [Fact]
    public async Task RunAsync_CaptureWithOutput_PassesFilePathToCaptureService()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", It.IsAny<CaptureRegion?>(), @"C:\shots\shot.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"artifact\":{\"metadata\":{\"artifactId\":\"artifact-1\"}}}");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\\.\DISPLAY1", "--output", @"C:\shots\shot.png"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(
            service => service.CaptureMonitorAsync(@"\\.\DISPLAY1", It.IsAny<CaptureRegion?>(), @"C:\shots\shot.png", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_RecordWithOutput_PassesFilePathToRecordingRequest()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        var directRecordingService = new Mock<IDirectRecordingService>();
        DirectRecordingRequest? capturedRequest = null;
        directRecordingService
            .Setup(service => service.Start(It.IsAny<DirectRecordingRequest>()))
            .Callback<DirectRecordingRequest>(request => capturedRequest = request)
            .Returns(new DirectRecordingResult(false, ErrorCode: "stop_here", ErrorMessage: "Stop before recording."));
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        await application.RunAsync(["record", "--monitor", @"\\.\DISPLAY1", "--seconds", "1", "-o", @"C:\clips\take1.mp4"]);

        Assert.Equal(@"C:\clips\take1.mp4", capturedRequest?.OutputPath);
    }

    [Fact]
    public async Task RunAsync_CaptureWithDirectoryOutput_ReportsInvalidOutputPathNotTargetNotFound()
    {
        // A rejected --output value reaches the CLI as an ArgumentException, exactly like a missing
        // monitor does. Without separating them by parameter name a script would be told the capture
        // target could not be found, when the real problem is the path it asked to write to.
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(It.IsAny<string>(), It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("The output path is a directory; supply a file path.", "outputPath"));
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture", "--monitor", @"\.\DISPLAY1", "--output", @"C:\shots\"]);

        Assert.Equal(1, exitCode);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(standardOutput.ToString());
        Assert.Equal("invalid_output_path", response?.Error?.Code);
    }

    [Fact]
    public async Task RunAsync_CaptureWithMissingMonitor_StillReportsTargetNotFound()
    {
        // Guards the other half of the split above: an ArgumentException that is not about outputPath
        // must keep its original target_not_found classification.
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureMonitorAsync(It.IsAny<string>(), It.IsAny<CaptureRegion?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("The monitor was not found.", "monitorName"));
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        await application.RunAsync(["capture", "--monitor", @"\.\NOPE"]);

        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(standardOutput.ToString());
        Assert.Equal("target_not_found", response?.Error?.Code);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task StaticRunAsync_HelpOrVersion_SucceedsWithoutConstructingCaptureOrRecordingServices(string flag)
    {
        // Regression test for a PR review comment: the static entry point used to construct
        // DirectCaptureService/WindowsOcrEngineService/DirectRecordingService before parsing args,
        // so --help/--version could fail (or do unnecessary work) even though they never need those
        // services. Parsing now happens first, and help/version short-circuit before any service
        // is constructed; this exercises that path end-to-end through the real static entry point.
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var exitCode = await CliApplication.RunAsync([flag], standardOutput, standardError);

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        Assert.NotEmpty(standardOutput.ToString());
    }

    [Fact]
    public async Task StaticRunAsync_UsageError_ReturnsExitCodeTwoWithoutConstructingServices()
    {
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["capture"], standardOutput, standardError);

        Assert.Equal(2, exitCode);
        Assert.Contains("requires --monitor", standardError.ToString());
    }

    [Fact]
    public void TryParse_Windows_UsesWindowsListCommand()
    {
        var parsed = CliCommandParser.TryParse(["windows"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("windows", command.Name);
    }

    [Fact]
    public void TryParse_CaptureWindow_RequiresWindowId()
    {
        var parsed = CliCommandParser.TryParse(["capture-window", "--window-id", "12345"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture-window", command.Name);
        Assert.Equal(12345L, command.WindowId);
    }

    [Fact]
    public void TryParse_CaptureWindow_AcceptsShortAlias()
    {
        var parsed = CliCommandParser.TryParse(["capture-window", "-w", "12345"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture-window", command.Name);
        Assert.Equal(12345L, command.WindowId);
    }

    [Fact]
    public void TryParse_CaptureWindow_AcceptsInlineFlagValue()
    {
        var parsed = CliCommandParser.TryParse(["capture-window", "--window-id=12345"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("capture-window", command.Name);
        Assert.Equal(12345L, command.WindowId);
    }

    [Fact]
    public void TryParse_OcrWindow_RequiresWindowId()
    {
        var parsed = CliCommandParser.TryParse(["ocr-window", "--window-id", "12345"], out var command, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal("ocr-window", command.Name);
        Assert.Equal(12345L, command.WindowId);
    }

    [Fact]
    public void TryParse_CaptureWindowWithoutWindowId_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["capture-window"], out _, out var error);

        Assert.False(parsed);
        Assert.Contains("--window-id", error);
    }

    [Fact]
    public void TryParse_OcrWindowWithoutWindowId_ReturnsUsageError()
    {
        var parsed = CliCommandParser.TryParse(["ocr-window"], out _, out var error);

        Assert.False(parsed);
        Assert.Contains("--window-id", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void TryParse_CaptureWindowWithInvalidWindowId_ReturnsUsageError(string windowIdValue)
    {
        var parsed = CliCommandParser.TryParse(["capture-window", "--window-id", windowIdValue], out _, out var error);

        Assert.False(parsed);
        Assert.Contains("--window-id", error);
    }

    [Fact]
    public async Task RunAsync_Windows_WritesWindowDescriptorsJson()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.ListWindows())
            .Returns("[{\"hwnd\":12345}]");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["windows"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.ListWindows(), Times.Once);
        Assert.Contains("12345", standardOutput.ToString());
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_CaptureWindow_DelegatesToCaptureWindowAsync()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureWindowAsync(12345L, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"artifact\":{\"metadata\":{\"artifactId\":\"window-artifact\"}}}");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["capture-window", "--window-id", "12345"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.CaptureWindowAsync(12345L, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("window-artifact", standardOutput.ToString());
    }

    [Fact]
    public async Task RunAsync_OcrWindow_DelegatesToCaptureWindowTextAsync()
    {
        var directCaptureService = new Mock<IDirectCaptureService>();
        directCaptureService
            .Setup(service => service.CaptureWindowTextAsync(12345L, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"recognizedText\":\"Hello\"}");
        var directRecordingService = new Mock<IDirectRecordingService>();
        var standardOutput = new StringWriter();
        var standardError = new StringWriter();
        var application = new CliApplication(directCaptureService.Object, directRecordingService.Object, standardOutput, standardError);

        var exitCode = await application.RunAsync(["ocr-window", "--window-id", "12345"]);

        Assert.Equal(0, exitCode);
        directCaptureService.Verify(service => service.CaptureWindowTextAsync(12345L, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("Hello", standardOutput.ToString());
    }
}
