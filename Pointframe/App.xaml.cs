using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pointframe.Automation;
using Pointframe.Data.Abstractions;
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
    private IActivationTelemetryService _activationTelemetry = null!;
    private IEventSubscription? _updateAvailableSubscription;
    private IEventSubscription? _recordingCompletedSubscription;
    private IEventSubscription? _captureCompletedSubscription;
    private ITelemetryService _telemetry = null!;
    private DateTime _sessionStartTime;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private LibraryWindow? _libraryWindow;

    private const string AutomationOpenImagePathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OPEN_IMAGE_PATH";

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupTimer = System.Diagnostics.Stopwatch.StartNew();
        var automationLaunchOptions = AutomationLaunchOptions.Parse(e.Args);
        base.OnStartup(e);
        _isAutomationMode = automationLaunchOptions.IsAutomationMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .Build();

        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .WriteTo.File(
                AppPaths.RollingLogPath,
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
            .ConfigureServices((_, services) => services.AddPointframeAppServices())
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
        _activationTelemetry = _host.Services.GetRequiredService<IActivationTelemetryService>();

        try
        {
            ApplyDataMigrations();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database migration failed at startup");
            _messageBox.ShowError(
                "Pointframe could not start because database migration failed. Please check logs for details.",
                "Startup Error");
            Current.Shutdown();
            return;
        }

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
            _sessionStartTime = DateTime.UtcNow;
            _telemetry.TrackEvent("app_started", new Dictionary<string, string>
            {
                ["version"] = version.ToString(),
                ["os_build"] = Environment.OSVersion.Version.ToString(),
                ["screen_count"] = System.Windows.Forms.Screen.AllScreens.Length.ToString(),
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
            onNewSnip: () => _captureLaunch.StartRegionSnip("tray"),
            onWholeScreenSnip: () => _captureLaunch.StartWholeScreenSnip("tray"),
            onCleanWindowSnip: () => _captureLaunch.StartCleanWindowSnip("tray"),
            onOpenImage: () => Dispatcher.InvokeAsync(OpenImage, System.Windows.Threading.DispatcherPriority.ApplicationIdle),
            onTrimRecording: ShowTrimWindow,
            onShowSettings: ShowSettingsWindow,
            onShowAbout: ShowAboutWindow,
            onShowLibrary: ShowLibraryWindow);
        _trayIconManager.Initialize();
        startupTimer.Stop();
        _telemetry.TrackEvent("startup_completed", new Dictionary<string, string>
        {
            ["duration_ms"] = startupTimer.ElapsedMilliseconds.ToString(),
        });
#if DEBUG
        _trayIconManager.AddDebugMenuItems();
#endif
        _globalHotkey.RegionSnipRequested += () => _captureLaunch.StartRegionSnip("hotkey");
        _globalHotkey.WholeScreenSnipRequested += () => _captureLaunch.StartWholeScreenSnip("hotkey");
        _globalHotkey.WholeScreenRecordRequested += _captureLaunch.StartWholeScreenRecord;
        _globalHotkey.CleanWindowSnipRequested += () => _captureLaunch.StartCleanWindowSnip("hotkey");
        _globalHotkey.Register();
        _logger.LogInformation("Global hotkey registered");
        _host.StartAsync().GetAwaiter().GetResult();
    }

    private void ApplyDataMigrations()
    {
        using var scope = _host.Services.CreateScope();
        var migrationService = scope.ServiceProvider.GetRequiredService<IMigrationService>();
        migrationService.ApplyMigrations().GetAwaiter().GetResult();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Pointframe shutting down");
        if (!_isAutomationMode && _sessionStartTime != default)
        {
            _telemetry?.TrackEvent("app_closed", new Dictionary<string, string>
            {
                ["session_minutes"] = ((int)(DateTime.UtcNow - _sessionStartTime).TotalMinutes).ToString(),
            });
        }

        _updateAvailableSubscription?.Dispose();
        _recordingCompletedSubscription?.Dispose();
        _captureCompletedSubscription?.Dispose();
        _globalHotkey.Dispose();
        _trayIconManager?.Dispose();
        _host.StopAsync().GetAwaiter().GetResult();
        _telemetry?.Flush();
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

        if (automationLaunchOptions.OpenLibraryWindow)
        {
            ShowLibraryWindow();
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

    private void ShowTrimWindow(string inputPath)
    {
        _trayIconManager.DismissTransientUi();

        var vm = _host.Services.GetRequiredService<Func<string, TrimViewModel>>()(inputPath);
        vm.TrimCompleted += (outputPath, trimmedDuration) =>
            _trayIconManager.HandleRecordingCompleted(outputPath, trimmedDuration.ToString(@"mm\:ss"));

        var window = new TrimWindow(vm);
        RegisterAutomationWindow(window);
        window.Show();
    }

    private void ShowSettingsWindow() => ShowOrActivateWindow(_settingsWindow, window => _settingsWindow = window);

    private void ShowAboutWindow() => ShowOrActivateWindow(_aboutWindow, window => _aboutWindow = window);

    private void ShowLibraryWindow() => ShowOrActivateWindow(
        _libraryWindow,
        window => _libraryWindow = window,
        window => window.ViewModel.RequestOpen += OpenCaptureFromLibrary);

    private void ShowOrActivateWindow<TWindow>(TWindow? current, Action<TWindow?> store, Action<TWindow>? initialize = null)
        where TWindow : Window
    {
        if (current is not null)
        {
            current.Activate();
            return;
        }

        var window = _host.Services.GetRequiredService<TWindow>();
        initialize?.Invoke(window);
        RegisterAutomationWindow(window);
        window.Closed += (_, _) => store(null);
        store(window);
        window.Show();
    }

    private void OpenCaptureFromLibrary(CaptureItem item)
    {
        try
        {
            var bitmap = _imageFileService.LoadForAnnotation(item.FilePath);
            _telemetry.TrackEvent("library_open_used");

            // Close the library before the full-screen overlay appears so the two never overlap.
            _libraryWindow?.Close();
            ShowOverlayFromImage(bitmap, item.FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Failed to open capture '{Path}'", item.FilePath);
            _messageBox.ShowWarning(ex.Message, "Open Capture");
        }
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

    private async ValueTask HandleUpdateAvailable(UpdateAvailableMessage message)
    {
        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _trayIconManager?.HandleUpdateAvailable(message.Result);
        }
        else
        {
            await dispatcher.InvokeAsync(() => _trayIconManager?.HandleUpdateAvailable(message.Result));
        }

        var v = message.Result.LatestVersion;
        _telemetry.TrackEvent("update_available", new Dictionary<string, string> { ["version"] = $"{v.Major}.{v.Minor}.{v.Build}" });
    }

    private ValueTask HandleRecordingCompleted(RecordingCompletedMessage message)
    {
        _trayIconManager.HandleRecordingCompleted(message.OutputPath, message.ElapsedText);
        _activationTelemetry.TrackRecordingCompleted(message.ElapsedText);
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleCaptureCompleted(CaptureCompletedMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.OutputPath))
        {
            _trayIconManager.HandleCaptureCompleted(message.OutputPath);
        }

        _activationTelemetry.TrackCaptureCompleted(message.CaptureAction);
        return ValueTask.CompletedTask;
    }

    private void EnsureInstallId()
    {
        if (string.IsNullOrEmpty(_userSettings.Current.InstallId))
        {
            try
            {
                _userSettings.Update(s =>
                {
                    s.InstallId = Guid.NewGuid().ToString("N");
                    s.InstallCreatedUtc = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist install ID; telemetry will use an in-memory ID for this session.");
                _userSettings.Current.InstallId = Guid.NewGuid().ToString("N");
                _userSettings.Current.InstallCreatedUtc = DateTime.UtcNow;
            }

            return;
        }
    }
}

