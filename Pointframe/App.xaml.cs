using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pointframe.Automation;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Serilog;
using Application = System.Windows.Application;

namespace Pointframe;

public partial class App : Application
{
    private IHost _host = null!;
    private bool _isAutomationMode;
    private ILogger<App>? _logger;
    private IMessageBoxService _messageBox = null!;
    private IUserSettingsService _userSettings = null!;
    private IThemeService _themeService = null!;
    private IAutoUpdateService _autoUpdate = null!;
    private IDialogService _dialogService = null!;
    private IImageFileService _imageFileService = null!;
    private IGlobalHotkeyService _globalHotkey = null!;
    private IAppErrorHandler _errorHandler = null!;
    private ITrayIconManager _trayIconManager = null!;
    private ICaptureLaunchService _captureLaunch = null!;
    private IEventSubscription? _updateAvailableSubscription;
    private IEventSubscription? _recordingCompletedSubscription;
    private IEventSubscription? _captureCompletedSubscription;
    private ITelemetryService _telemetry = null!;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;

    private const string AutomationOpenImagePathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OPEN_IMAGE_PATH";

    protected override void OnStartup(StartupEventArgs e)
    {
        var automationLaunchOptions = AutomationLaunchOptions.Parse(e.Args);
        base.OnStartup(e);
        _isAutomationMode = automationLaunchOptions.IsAutomationMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .Build();

        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe", "logs", "pointframe-.log");

        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: config.GetValue<int>("Logging:RetainedFileCountLimit", 7),
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .CreateLogger();

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(dispose: false);
            })
            .ConfigureServices((_, services) => ConfigureServices(services))
            .Build();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _messageBox = _host.Services.GetRequiredService<IMessageBoxService>();
        _userSettings = _host.Services.GetRequiredService<IUserSettingsService>();
        _themeService = _host.Services.GetRequiredService<IThemeService>();
        _dialogService = _host.Services.GetRequiredService<IDialogService>();
        _imageFileService = _host.Services.GetRequiredService<IImageFileService>();
        _globalHotkey = _host.Services.GetRequiredService<IGlobalHotkeyService>();
        _errorHandler = _host.Services.GetRequiredService<IAppErrorHandler>();
        _captureLaunch = _host.Services.GetRequiredService<ICaptureLaunchService>();
        _telemetry = _host.Services.GetRequiredService<ITelemetryService>();
        _themeService.Apply(_userSettings.Current.Theme);
        if (!automationLaunchOptions.IsAutomationMode)
        {
            var eventAggregator = _host.Services.GetRequiredService<IEventAggregator>();
            _updateAvailableSubscription = eventAggregator.Subscribe<UpdateAvailableMessage>(HandleUpdateAvailable);
            _recordingCompletedSubscription = eventAggregator.Subscribe<RecordingCompletedMessage>(HandleRecordingCompleted);
            _captureCompletedSubscription = eventAggregator.Subscribe<CaptureCompletedMessage>(HandleCaptureCompleted);
            _autoUpdate = _host.Services.GetRequiredService<IAutoUpdateService>();
        }

        _logger.LogInformation("Pointframe starting up");

        EnsureInstallId();

        if (!automationLaunchOptions.IsAutomationMode)
        {
            var version = _host.Services.GetRequiredService<IAppVersionService>().Current;
            _telemetry.TrackEvent("app_started", new Dictionary<string, string>
            {
                ["version"] = version.ToString(),
                ["os_build"] = Environment.OSVersion.Version.ToString(),
            });
        }

        _errorHandler.Register();

        if (automationLaunchOptions.IsAutomationMode)
        {
            _logger.LogInformation("Pointframe automation mode enabled");
            ShowAutomationWindow(automationLaunchOptions);
            return;
        }

        _trayIconManager = new TrayIconManager(
            _host.Services.GetRequiredService<ILogger<TrayIconManager>>(),
            _messageBox,
            _host.Services.GetRequiredService<IProcessService>(),
            _host.Services.GetRequiredService<IUpdateService>(),
            _host.Services.GetRequiredService<IAppVersionService>(),
            _autoUpdate,
            _userSettings,
            _host.Services.GetRequiredService<IGifExportService>(),
            _telemetry,
            onNewSnip: _captureLaunch.StartRegionSnip,
            onWholeScreenSnip: _captureLaunch.StartWholeScreenSnip,
            onOpenImage: () => Dispatcher.InvokeAsync(OpenImage, System.Windows.Threading.DispatcherPriority.ApplicationIdle),
            onShowSettings: ShowSettingsWindow,
            onShowAbout: ShowAboutWindow);
        _trayIconManager.Initialize();
#if DEBUG
        _trayIconManager.AddDebugMenuItems();
#endif
        _globalHotkey.RegionSnipRequested += _captureLaunch.StartRegionSnip;
        _globalHotkey.WholeScreenSnipRequested += _captureLaunch.StartWholeScreenSnip;
        _globalHotkey.WholeScreenRecordRequested += _captureLaunch.StartWholeScreenRecord;
        _globalHotkey.Register();
        _logger.LogInformation("Global hotkey registered");
        _host.StartAsync().GetAwaiter().GetResult();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ITelemetryService, TelemetryService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IImageFileService, ImageFileService>();
        services.AddSingleton<IEventAggregator, DefaultEventAggregator>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IMouseHookService, MouseHookService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IMicrophoneDeviceService, MicrophoneDeviceService>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<IAppErrorHandler, AppErrorHandler>();
        services.AddSingleton<ICaptureLaunchService, CaptureLaunchService>();
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddTransient<IVideoWriterFactory, VideoWriterFactory>();
        services.AddTransient<IScreenRecordingService, ScreenRecordingService>();
        services.AddSingleton<IGifExportService, GifExportService>();
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddSingleton<IOcrService, WindowsOcrService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<RecordingAnnotationViewModel>();
        services.AddTransient<OverlayWindow>(CreateOverlayWindow);
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<Func<IScreenRecordingService, string, RecordingHudViewModel>>(sp =>
            (screenRecordingService, outputPath) => new RecordingHudViewModel(
                screenRecordingService,
                outputPath,
                sp.GetRequiredService<IEventAggregator>(),
                sp.GetRequiredService<ILogger<RecordingHudViewModel>>()));
        services.AddTransient<AboutViewModel>();
        services.AddTransient<AboutWindow>();
        services.AddTransient<UpdateDownloadViewModel>(sp =>
            new UpdateDownloadViewModel(
                UpdateDownloadViewModel.SharedHttp,
                sp.GetRequiredService<IProcessService>(),
                sp.GetService<ILogger<UpdateDownloadViewModel>>()));
        services.AddTransient<Func<UpdateDownloadViewModel>>(sp => () => sp.GetRequiredService<UpdateDownloadViewModel>());
        services.AddTransient<Func<UpdateDownloadViewModel, UpdateDownloadWindow>>(_ => vm => new UpdateDownloadWindow(vm));
        services.AddTransient<IUpdateDownloadService, UpdateDownloadWindowService>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
        services.AddSingleton<AutoUpdateService>();
        services.AddSingleton<IAutoUpdateService>(sp => sp.GetRequiredService<AutoUpdateService>());
        services.AddHostedService(sp => sp.GetRequiredService<AutoUpdateService>());
    }

    private static OverlayWindow CreateOverlayWindow(IServiceProvider sp) => new(
        sp.GetRequiredService<OverlayViewModel>(),
        sp.GetRequiredService<IScreenCaptureService>(),
        sp.GetRequiredService<IScreenRecordingService>(),
        sp.GetRequiredService<IMouseHookService>(),
        sp.GetRequiredService<Func<IScreenRecordingService, string, RecordingHudViewModel>>(),
        sp.GetRequiredService<IEventAggregator>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<IUserSettingsService>(),
        sp.GetRequiredService<IMessageBoxService>(),
        sp.GetRequiredService<IFileSystemService>(),
        sp.GetRequiredService<IOcrService>(),
        sp.GetRequiredService<ITelemetryService>(),
        sp.GetRequiredService<RecordingAnnotationViewModel>());

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Pointframe shutting down");
        _updateAvailableSubscription?.Dispose();
        _recordingCompletedSubscription?.Dispose();
        _captureCompletedSubscription?.Dispose();
        _globalHotkey.Dispose();
        _trayIconManager?.Dispose();
        _telemetry?.Flush();
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        base.OnExit(e);
        Log.CloseAndFlush();
    }

    private void ShowAutomationWindow(AutomationLaunchOptions automationLaunchOptions)
    {
        if (automationLaunchOptions.OpenSettingsWindow)
        {
            ShowSettingsWindow();
            return;
        }

        if (automationLaunchOptions.OpenAboutWindow)
        {
            ShowAboutWindow();
            return;
        }

        if (automationLaunchOptions.OpenSampleOverlayWindow)
        {
            ShowAutomationSampleOverlayWindow();
            return;
        }

        if (automationLaunchOptions.OpenSampleRecordingOverlayWindow)
        {
            ShowAutomationSampleRecordingOverlayWindow();
            return;
        }

        if (automationLaunchOptions.OpenTraySampleOverlayWindow)
        {
            ShowAutomationTraySampleOverlayWindow();
            return;
        }

        Current.Shutdown();
    }

    private void OpenImage()
    {
        var selectedPath = _isAutomationMode
            ? Environment.GetEnvironmentVariable(AutomationOpenImagePathEnvironmentVariable)
            : null;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            selectedPath = _dialogService.PickOpenImageFile();
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        try
        {
            var bitmap = _imageFileService.LoadForAnnotation(selectedPath);
            _telemetry.TrackEvent("open_image_used");
            ShowOverlayFromImage(bitmap, selectedPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to open image '{Path}'", selectedPath);
            _messageBox.ShowWarning(ex.Message, "Open Image");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected failure while opening image '{Path}'", selectedPath);
            _messageBox.ShowError(
                "The selected image could not be opened. Please try a different file.",
                "Open Image");
        }
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _host.Services.GetRequiredService<SettingsWindow>();
        RegisterAutomationWindow(_settingsWindow);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ShowAboutWindow()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = _host.Services.GetRequiredService<AboutWindow>();
        RegisterAutomationWindow(_aboutWindow);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    private void ShowAutomationSampleOverlayWindow()
    {
        var (bitmap, sourcePath) = AutomationSampleFactory.CreateOpenedImageSample();
        ShowOverlayFromImage(bitmap, sourcePath);
    }

    private void ShowAutomationSampleRecordingOverlayWindow()
    {
        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        RegisterAutomationWindow(overlay);
        overlay.InitializeFromSelectionSession(AutomationSampleFactory.CreateRecordingSelectionSample());
        DpiAwarenessScope.RunPerMonitorV2(() => overlay.Show());
    }

    private void ShowAutomationTraySampleOverlayWindow()
    {
        AutomationSampleFactory.CreateOpenedImageSample();
        OpenImage();
    }

    private void ShowOverlayFromImage(BitmapSource bitmap, string sourcePath)
    {
        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        RegisterAutomationWindow(overlay);
        overlay.InitializeFromImage(bitmap, sourcePath);
        overlay.Show();
    }

    internal void RegisterAutomationWindow(Window window)
    {
        if (!_isAutomationMode)
        {
            return;
        }

        window.Closed -= OnAutomationWindowClosed;
        window.Closed += OnAutomationWindowClosed;
    }

    private void OnAutomationWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Closed -= OnAutomationWindowClosed;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() =>
        {
            if (!_isAutomationMode)
            {
                return;
            }

            if (!Current.Windows.OfType<Window>().Any(window => window.IsVisible))
            {
                Current.Shutdown();
            }
        }));
    }

    private ValueTask HandleUpdateAvailable(UpdateAvailableMessage message)
    {
        var v = message.Result.LatestVersion;
        _telemetry.TrackEvent("update_available", new Dictionary<string, string> { ["version"] = $"{v.Major}.{v.Minor}.{v.Build}" });
        _trayIconManager.HandleUpdateAvailable(message.Result);
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRecordingCompleted(RecordingCompletedMessage message)
    {
        _trayIconManager.HandleRecordingCompleted(message.OutputPath, message.ElapsedText);
        _telemetry.TrackEvent("recording_completed");
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleCaptureCompleted(CaptureCompletedMessage message)
    {
        _trayIconManager.HandleCaptureCompleted(message.OutputPath);
        _telemetry.TrackEvent("capture_completed", new Dictionary<string, string>
        {
            ["action"] = "copy",
        });
        return ValueTask.CompletedTask;
    }

    private void EnsureInstallId()
    {
        if (string.IsNullOrEmpty(_userSettings.Current.InstallId))
        {
            try
            {
                _userSettings.Update(s => s.InstallId = Guid.NewGuid().ToString("N"));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist install ID; telemetry will use an in-memory ID for this session.");
                _userSettings.Current.InstallId = Guid.NewGuid().ToString("N");
            }
        }
    }
}

