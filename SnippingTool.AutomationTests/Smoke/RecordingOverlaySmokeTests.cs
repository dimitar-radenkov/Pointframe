using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SnippingTool.AutomationTests.Fixtures;
using SnippingTool.AutomationTests.Support;
using Xunit;

namespace SnippingTool.AutomationTests.Smoke;

public sealed class RecordingOverlaySmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private static readonly TimeSpan RecordingDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DurationSlack = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DurationPaddingAllowance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly DesktopAutomationFixture _fixture;

    public RecordingOverlaySmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void StartAndStopRecording_WritesRecordingFileToAutomationOutput()
    {
        _fixture.EnsureRecordingBackendAvailable();
        _fixture.SeedSettings(
            autoSaveScreenshots: false,
            recordingFps: 10);

        using (var app = AutomationApp.Launch("--automation-open-sample-recording-overlay", _fixture.CreateEnvironmentVariables()))
        {
            Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);

            app.ClickFirstButton(
                AutomationIds.OverlayWindowRecord,
                AutomationIds.OverlayWindowCompactRecord);

            app.SwitchToTopLevelWindow(AutomationIds.RecordingOverlayWindowRoot);
            Assert.Equal(AutomationIds.RecordingOverlayWindowRoot, app.MainWindowAutomationId);
            Assert.NotNull(app.FindRequiredElement(AutomationIds.RecordingOverlayWindowStop));

            Thread.Sleep(TimeSpan.FromSeconds(1));

            app.ClickButton(AutomationIds.RecordingOverlayWindowStop);
            app.WaitForExit();
        }

        var recordingFiles = Directory.GetFiles(_fixture.RecordingOutputPath, "SnipRec-*.mp4");
        Assert.Single(recordingFiles);
        Assert.True(new FileInfo(recordingFiles[0]).Length > 0);
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void StartAndStopRecording_PreservesRecordedDuration()
    {
        _fixture.EnsureRecordingBackendAvailable();
        _fixture.SeedSettings(
            autoSaveScreenshots: false,
            recordingFps: 10);

        TimeSpan elapsedBeforeStop;
        using (var app = AutomationApp.Launch("--automation-open-sample-recording-overlay", _fixture.CreateEnvironmentVariables()))
        {
            Assert.Equal(AutomationIds.OverlayWindowRoot, app.MainWindowAutomationId);

            app.ClickFirstButton(
                AutomationIds.OverlayWindowRecord,
                AutomationIds.OverlayWindowCompactRecord);

            app.SwitchToTopLevelWindow(AutomationIds.RecordingOverlayWindowRoot);
            Assert.Equal(AutomationIds.RecordingOverlayWindowRoot, app.MainWindowAutomationId);
            Assert.NotNull(app.FindRequiredElement(AutomationIds.RecordingOverlayWindowStop));

            var stopwatch = Stopwatch.StartNew();
            Thread.Sleep(RecordingDuration);
            elapsedBeforeStop = stopwatch.Elapsed;

            app.ClickButton(AutomationIds.RecordingOverlayWindowStop);
            app.WaitForExit();
        }

        var recordingFiles = Directory.GetFiles(_fixture.RecordingOutputPath, "SnipRec-*.mp4");
        Assert.Single(recordingFiles);

        var actualDuration = GetMediaDuration(recordingFiles[0]);
        var minimumExpectedDuration = elapsedBeforeStop - DurationSlack;
        var maximumExpectedDuration = elapsedBeforeStop + DurationPaddingAllowance;

        Assert.True(actualDuration >= minimumExpectedDuration,
            $"Expected recording duration >= {minimumExpectedDuration}, but got {actualDuration} for elapsed wall-clock time {elapsedBeforeStop}.");
        Assert.True(actualDuration <= maximumExpectedDuration,
            $"Expected recording duration <= {maximumExpectedDuration}, but got {actualDuration} for elapsed wall-clock time {elapsedBeforeStop}.");
    }

    private TimeSpan GetMediaDuration(string recordingPath)
    {
        var ffmpegPath = _fixture.ResolveRecordingBackendPath();
        var startInfo = new ProcessStartInfo(ffmpegPath, $"-i \"{recordingPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg for media duration probing.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)ProbeTimeout.TotalMilliseconds);
            throw new TimeoutException($"ffmpeg did not finish probing media duration within {ProbeTimeout.TotalSeconds} seconds.");
        }

        var stderr = stderrTask.GetAwaiter().GetResult();

        var match = Regex.Match(stderr, @"Duration:\s*(\d{2}:\d{2}:\d{2}\.\d{2})");
        Assert.True(match.Success, $"ffmpeg output did not contain a media duration. Output:{Environment.NewLine}{stderr}");

        return TimeSpan.ParseExact(match.Groups[1].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);
    }
}
