namespace Pointframe.Automation;

internal sealed class AutomationLaunchOptions
{
    private const string OpenSettingsArgument = "--automation-open-settings";
    private const string OpenAboutArgument = "--automation-open-about";
    private const string OpenSampleOverlayArgument = "--automation-open-sample-overlay";
    private const string OpenSampleRecordingOverlayArgument = "--automation-open-sample-recording-overlay";
    private const string OpenTraySampleOverlayArgument = "--automation-open-tray-sample-overlay";

    private AutomationLaunchOptions(
        bool openSettingsWindow,
        bool openAboutWindow,
        bool openSampleOverlayWindow,
        bool openSampleRecordingOverlayWindow,
        bool openTraySampleOverlayWindow)
    {
        OpenSettingsWindow = openSettingsWindow;
        OpenAboutWindow = openAboutWindow;
        OpenSampleOverlayWindow = openSampleOverlayWindow;
        OpenSampleRecordingOverlayWindow = openSampleRecordingOverlayWindow;
        OpenTraySampleOverlayWindow = openTraySampleOverlayWindow;
    }

    public bool IsAutomationMode =>
        OpenSettingsWindow
        || OpenAboutWindow
        || OpenSampleOverlayWindow
        || OpenSampleRecordingOverlayWindow
        || OpenTraySampleOverlayWindow;

    public bool OpenSettingsWindow { get; }

    public bool OpenAboutWindow { get; }

    public bool OpenSampleOverlayWindow { get; }

    public bool OpenSampleRecordingOverlayWindow { get; }

    public bool OpenTraySampleOverlayWindow { get; }

    public static AutomationLaunchOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsedArguments = args.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new AutomationLaunchOptions(
            parsedArguments.Contains(OpenSettingsArgument),
            parsedArguments.Contains(OpenAboutArgument),
            parsedArguments.Contains(OpenSampleOverlayArgument),
            parsedArguments.Contains(OpenSampleRecordingOverlayArgument),
            parsedArguments.Contains(OpenTraySampleOverlayArgument));
    }
}
