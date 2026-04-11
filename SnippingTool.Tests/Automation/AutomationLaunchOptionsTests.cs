using SnippingTool.Automation;
using Xunit;

namespace SnippingTool.Tests.Automation;

public sealed class AutomationLaunchOptionsTests
{
    [Theory]
    [InlineData("--automation-open-settings", true, false, false, false, false)]
    [InlineData("--automation-open-about", false, true, false, false, false)]
    [InlineData("--automation-open-sample-overlay", false, false, true, false, false)]
    [InlineData("--automation-open-sample-recording-overlay", false, false, false, true, false)]
    [InlineData("--automation-open-tray-sample-overlay", false, false, false, false, true)]
    public void Parse_WhenSingleAutomationArgumentIsPresent_SetsMatchingFlag(
        string argument,
        bool openSettingsWindow,
        bool openAboutWindow,
        bool openSampleOverlayWindow,
        bool openSampleRecordingOverlayWindow,
        bool openTraySampleOverlayWindow)
    {
        var options = AutomationLaunchOptions.Parse([argument]);

        Assert.Equal(openSettingsWindow, options.OpenSettingsWindow);
        Assert.Equal(openAboutWindow, options.OpenAboutWindow);
        Assert.Equal(openSampleOverlayWindow, options.OpenSampleOverlayWindow);
        Assert.Equal(openSampleRecordingOverlayWindow, options.OpenSampleRecordingOverlayWindow);
        Assert.Equal(openTraySampleOverlayWindow, options.OpenTraySampleOverlayWindow);
        Assert.True(options.IsAutomationMode);
    }

    [Fact]
    public void Parse_WhenMultipleAutomationArgumentsArePresent_SetsAllMatchingFlags()
    {
        var options = AutomationLaunchOptions.Parse(
        [
            "--automation-open-settings",
            "--automation-open-sample-recording-overlay",
            "--automation-open-tray-sample-overlay",
        ]);

        Assert.True(options.OpenSettingsWindow);
        Assert.True(options.OpenSampleRecordingOverlayWindow);
        Assert.True(options.OpenTraySampleOverlayWindow);
        Assert.True(options.IsAutomationMode);
    }

    [Fact]
    public void Parse_WhenNoAutomationArgumentsArePresent_DisablesAutomationMode()
    {
        var options = AutomationLaunchOptions.Parse(Array.Empty<string>());

        Assert.False(options.OpenSettingsWindow);
        Assert.False(options.OpenAboutWindow);
        Assert.False(options.OpenSampleOverlayWindow);
        Assert.False(options.OpenSampleRecordingOverlayWindow);
        Assert.False(options.OpenTraySampleOverlayWindow);
        Assert.False(options.IsAutomationMode);
    }
}
