using System.Globalization;
using System.Windows;

namespace Pointframe.Automation;

internal sealed class AutomationLaunchOptions
{
    private const string OpenSettingsArgument = "--automation-open-settings";
    private const string OpenAboutArgument = "--automation-open-about";
    private const string OpenLibraryArgument = "--automation-open-library";
    private const string OpenSampleOverlayArgument = "--automation-open-sample-overlay";
    private const string OpenSampleRecordingOverlayArgument = "--automation-open-sample-recording-overlay";
    private const string OpenTraySampleOverlayArgument = "--automation-open-tray-sample-overlay";
    private const string StartScrollingSnipPrefix = "--automation-start-scrolling-snip=";

    private AutomationLaunchOptions(
        bool openSettingsWindow,
        bool openAboutWindow,
        bool openLibraryWindow,
        bool openSampleOverlayWindow,
        bool openSampleRecordingOverlayWindow,
        bool openTraySampleOverlayWindow,
        Int32Rect? scrollingCaptureRegionPixels)
    {
        OpenSettingsWindow = openSettingsWindow;
        OpenAboutWindow = openAboutWindow;
        OpenLibraryWindow = openLibraryWindow;
        OpenSampleOverlayWindow = openSampleOverlayWindow;
        OpenSampleRecordingOverlayWindow = openSampleRecordingOverlayWindow;
        OpenTraySampleOverlayWindow = openTraySampleOverlayWindow;
        ScrollingCaptureRegionPixels = scrollingCaptureRegionPixels;
    }

    public bool IsAutomationMode =>
        OpenSettingsWindow
        || OpenAboutWindow
        || OpenLibraryWindow
        || OpenSampleOverlayWindow
        || OpenSampleRecordingOverlayWindow
        || OpenTraySampleOverlayWindow
        || ScrollingCaptureRegionPixels is not null;

    public Int32Rect? ScrollingCaptureRegionPixels { get; }

    public bool OpenSettingsWindow { get; }

    public bool OpenAboutWindow { get; }

    public bool OpenLibraryWindow { get; }

    public bool OpenSampleOverlayWindow { get; }

    public bool OpenSampleRecordingOverlayWindow { get; }

    public bool OpenTraySampleOverlayWindow { get; }

    public static AutomationLaunchOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsedArguments = args.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scrollingArgument = parsedArguments.FirstOrDefault(argument => argument.StartsWith(StartScrollingSnipPrefix, StringComparison.OrdinalIgnoreCase));
        Int32Rect? scrollingRegion = scrollingArgument is null ? null : ParseScrollingRegion(scrollingArgument[StartScrollingSnipPrefix.Length..]);

        return new AutomationLaunchOptions(
            parsedArguments.Contains(OpenSettingsArgument),
            parsedArguments.Contains(OpenAboutArgument),
            parsedArguments.Contains(OpenLibraryArgument),
            parsedArguments.Contains(OpenSampleOverlayArgument),
            parsedArguments.Contains(OpenSampleRecordingOverlayArgument),
            parsedArguments.Contains(OpenTraySampleOverlayArgument),
            scrollingRegion);
    }

    private static Int32Rect ParseScrollingRegion(string value)
    {
        var parts = value.Split(',');
        if (parts.Length != 4 || !parts.All(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            throw new ArgumentException("The automation scrolling region must be x,y,width,height.", nameof(value));
        }

        var coordinates = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (coordinates[2] <= 0 || coordinates[3] <= 0)
        {
            throw new ArgumentException("The automation scrolling region must have positive dimensions.", nameof(value));
        }

        return new Int32Rect(coordinates[0], coordinates[1], coordinates[2], coordinates[3]);
    }
}
