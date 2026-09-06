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
    private readonly IWindowCaptureService _windowCaptureService;
    private readonly BeautifierRenderService _beautifierRenderService;

    public CaptureLaunchService(
        IServiceProvider services,
        IUserSettingsService userSettings,
        IMessageBoxService messageBox,
        IFileSystemService fileSystem,
        ILoggerFactory loggerFactory,
        ILogger<CaptureLaunchService> logger,
        ITelemetryService telemetry,
        IWindowCaptureService windowCaptureService,
        BeautifierRenderService beautifierRenderService)
    {
        _services = services;
        _userSettings = userSettings;
        _messageBox = messageBox;
        _fileSystem = fileSystem;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _telemetry = telemetry;
        _windowCaptureService = windowCaptureService;
        _beautifierRenderService = beautifierRenderService;
    }

    public void StartRegionSnip(string source = "tray")
    {
        _logger.LogDebug("Region snip started");
        _telemetry.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
            [TelemetryPropertyKeys.Source] = source,
        });
        LaunchCapture(wholeScreen: false);
    }

    public void StartWholeScreenSnip(string source = "tray")
    {
        _logger.LogDebug("Whole-screen snip started");
        _telemetry.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "whole_screen",
            [TelemetryPropertyKeys.Source] = source,
        });
        LaunchCapture(wholeScreen: true);
    }

    public void StartCleanWindowSnip(string source = "tray")
    {
        _logger.LogDebug("Clean window snip started");
        _telemetry.TrackEvent(TelemetryEvents.SnipStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "window_clean",
            [TelemetryPropertyKeys.Source] = source,
        });

        var delay = _userSettings.Current.CaptureDelaySeconds;
        if (delay > 0)
        {
            _telemetry.TrackEvent(TelemetryEvents.CaptureDelayUsed, new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.DelaySeconds] = delay.ToString(),
            });
            new CountdownWindow(delay, () => ExecuteCleanWindowSnip()).Show();
            return;
        }

        ExecuteCleanWindowSnip();
    }

    private void ExecuteCleanWindowSnip()
    {

        if (!_windowCaptureService.TryCaptureWindowUnderCursor(out var rawWindowBitmap) || rawWindowBitmap is null)
        {
            _messageBox.ShowWarning(
                "Could not capture the window under the cursor. Move the cursor over a visible app window and try again.",
                "Clean window snip");
            return;
        }

        var beautified = _beautifierRenderService.Render(
            rawWindowBitmap,
            BeautifyBackground.White,
            padding: 36,
            cornerRadius: 12,
            shadowEnabled: true,
            shadowBlur: 28,
            shadowOffsetY: 16,
            shadowOpacity: 0.5);

        var overlay = _services.GetRequiredService<OverlayWindow>();
        overlay.InitializeFromImage(beautified, "window-capture://under-cursor", SelectionSessionMode.WindowClean);
        DpiAwarenessScope.RunPerMonitorV2(() => overlay.Show());
    }

    private void LaunchCapture(bool wholeScreen)
    {
        var delay = _userSettings.Current.CaptureDelaySeconds;
        if (delay > 0)
        {
            _telemetry.TrackEvent(TelemetryEvents.CaptureDelayUsed, new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.DelaySeconds] = delay.ToString(),
            });
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
            _telemetry.TrackEvent(TelemetryEvents.SnipCancelled, new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.Type] = wholeScreen ? "whole_screen" : "region",
            });
            return;
        }

        var overlay = _services.GetRequiredService<OverlayWindow>();
        overlay.InitializeFromSelectionSession(selection);
        DpiAwarenessScope.RunPerMonitorV2(() => overlay.Show());
    }

    private void ShowWholeScreenOverlay(Forms.Screen targetScreen)
    {
        var screenCapture = _services.GetRequiredService<IScreenCaptureService>();
        var monitorScale = MonitorDpiHelper.GetMonitorScale(targetScreen.Bounds.Location);
        var hostBoundsPixels = new Int32Rect(
            targetScreen.Bounds.X,
            targetScreen.Bounds.Y,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height);
        var monitorSnapshot = screenCapture.Capture(
            targetScreen.Bounds.X,
            targetScreen.Bounds.Y,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height);
        var selection = SelectionSession.CreateWholeScreenSelectionResult(
            targetScreen.DeviceName,
            monitorSnapshot,
            MonitorDpiHelper.CalculateWindowBounds(targetScreen.Bounds, monitorScale),
            hostBoundsPixels,
            monitorScale,
            monitorScale);
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
            _telemetry.TrackEvent(TelemetryEvents.FfmpegMissing);
            _messageBox.ShowWarning(ex.Message, "ffmpeg not found");
            return;
        }

        _telemetry.TrackEvent(TelemetryEvents.RecordingStarted, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "whole_screen",
        });

        if (_userSettings.Current.RecordMicrophone && !recorder.IsRecordingMicrophoneEnabled)
        {
            _telemetry.TrackEvent(TelemetryEvents.MicrophoneUnavailable);
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
