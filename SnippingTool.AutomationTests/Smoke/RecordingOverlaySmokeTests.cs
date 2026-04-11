using SnippingTool.AutomationTests.Fixtures;
using SnippingTool.AutomationTests.Support;
using SnippingTool.Models;
using Xunit;

namespace SnippingTool.AutomationTests.Smoke;

public sealed class RecordingOverlaySmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public RecordingOverlaySmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public void StartAndStopRecording_WritesRecordingFileToAutomationOutput()
    {
        const RecordingFormat recordingFormat = RecordingFormat.Avi;
        _fixture.EnsureRecordingBackendAvailable(recordingFormat);
        _fixture.SeedSettings(
            autoSaveScreenshots: false,
            recordingFormat: recordingFormat,
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

        var recordingFiles = Directory.GetFiles(_fixture.RecordingOutputPath, "SnipRec-*.avi");
        Assert.Single(recordingFiles);
        Assert.True(new FileInfo(recordingFiles[0]).Length > 0);
    }
}
