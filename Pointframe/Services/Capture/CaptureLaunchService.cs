using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Forms = System.Windows.Forms;

namespace Pointframe.Services;

internal sealed class CaptureLaunchService : ICaptureLaunchService
{
    private readonly IServiceProvider _services;
    private readonly IUserSettingsService _userSettings;
    private readonly IMessageBoxService _messageBox;
    private readonly IFileSystemService _fileSystem;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CaptureLaunchService> _logger;
    private readonly ITelemetryService _telemetry;

    public CaptureLaunchService(
        IServiceProvider services,
        IUserSettingsService userSettings,
        IMessageBoxService messageBox,
        IFileSystemService fileSystem,
        ILoggerFactory loggerFactory,
        ILogger<CaptureLaunchService> logger,
        ITelemetryService telemetry)
    {
        _services = services;
        _userSettings = userSettings;
        _messageBox = messageBox;
        _fileSystem = fileSystem;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _telemetry = telemetry;
    }

    public void StartRegionSnip(string source = "tray")
    {
        _logger.LogDebug("Region snip started");
        _telemetry.TrackEvent("snip_started", new Dictionary<string, string> { ["type"] = "region", ["source"] = source });
        LaunchCapture(wholeScreen: false);
    }

    public void StartWholeScreenSnip(string source = "tray")
    {
        _logger.LogDebug("Whole-screen snip started");
        _telemetry.TrackEvent("snip_started", new Dictionary<string, string> { ["type"] = "whole_screen", ["source"] = source });
        LaunchCapture(wholeScreen: true);
    }

    private void LaunchCapture(bool wholeScreen)
    {
        var delay = _userSettings.Current.CaptureDelaySeconds;
        if (delay > 0)
        {
            _telemetry.TrackEvent("capture_delay_used", new Dictionary<string, string> { ["delay_seconds"] = delay.ToString() });
            new CountdownWindow(delay, () => ShowSelectionOverlay(wholeScreen)).Show();
            return;
        }

        ShowSelectionOverlay(wholeScreen);
    }

    private async void ShowSelectionOverlay(bool wholeScreen)
    {
        var screenCapture = _services.GetRequiredService<IScreenCaptureService>();
        var selection = wholeScreen
            ? await SelectionSession.SelectWholeScreenAsync(screenCapture, _loggerFactory)
            : await SelectionSession.SelectAsync(screenCapture, _loggerFactory);

        if (selection is null)
        {
            _telemetry.TrackEvent("snip_cancelled", new Dictionary<string, string> { ["type"] = wholeScreen ? "whole_screen" : "region" });
            return;
        }

        var overlay = _services.GetRequiredService<OverlayWindow>();
        overlay.InitializeFromSelectionSession(selection);
        DpiAwarenessScope.RunPerMonitorV2(() => overlay.Show());
    }

    public async void StartWholeScreenRecord()
    {
        _logger.LogDebug("Whole-screen record hotkey triggered");

        var screenCapture = _services.GetRequiredService<IScreenCaptureService>();
        var recorder = _services.GetRequiredService<IScreenRecordingService>();

        var targetScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var monitorScale = MonitorDpiHelper.GetMonitorScale(targetScreen.Bounds.Location);
        var hostBoundsPixels = new Int32Rect(
            targetScreen.Bounds.X,
            targetScreen.Bounds.Y,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height);
        var selectionRect = new Rect(
            0d,
            0d,
            targetScreen.Bounds.Width / monitorScale,
            targetScreen.Bounds.Height / monitorScale);

        var geometry = OverlayWindow.CreateRecordingSessionGeometry(
            selectionRect,
            hostBoundsPixels,
            targetScreen.DeviceName);

        var videosDir = _userSettings.Current.RecordingOutputPath;
        _fileSystem.CreateDirectory(videosDir);
        var path = _fileSystem.CombinePath(videosDir, $"SnipRec-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        try
        {
            await System.Threading.Tasks.Task.Run(() => recorder.Start(
                hostBoundsPixels.X,
                hostBoundsPixels.Y,
                hostBoundsPixels.Width,
                hostBoundsPixels.Height,
                path));
        }
        catch (FileNotFoundException ex)
        {
            _telemetry.TrackEvent("ffmpeg_missing");
            _messageBox.ShowWarning(ex.Message, "ffmpeg not found");
            return;
        }

        _telemetry.TrackEvent("recording_started", new Dictionary<string, string> { ["type"] = "whole_screen" });

        if (_userSettings.Current.RecordMicrophone && !recorder.IsRecordingMicrophoneEnabled)
        {
            _telemetry.TrackEvent("microphone_unavailable");
            _messageBox.ShowWarning(
                "Microphone recording is enabled, but no compatible microphone device was available. The recording will continue without microphone audio.",
                "Microphone unavailable");
        }

        RecordingOverlayWindow? recordingOverlay = null;
        DpiAwarenessScope.RunPerMonitorV2(() =>
        {
            recordingOverlay = new RecordingOverlayWindow(
                geometry,
                path,
                recorder,
                screenCapture,
                _services.GetRequiredService<IMouseHookService>(),
                _services.GetRequiredService<Func<IScreenRecordingService, string, RecordingHudViewModel>>(),
                _services.GetRequiredService<IEventAggregator>(),
                _loggerFactory,
                _userSettings,
                _services.GetRequiredService<RecordingAnnotationViewModel>());
        });

        if (recordingOverlay is null)
        {
            _logger.LogError("Failed to create recording overlay for whole-screen record hotkey");
            return;
        }

        DpiAwarenessScope.RunPerMonitorV2(() => recordingOverlay.Show());
    }
}
