using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SnippingTool.Automation;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;
using Application = System.Windows.Application;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace SnippingTool;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private IHost _host = null!;
    private bool _isAutomationMode;
    private ILogger<App>? _logger;
    private IMessageBoxService _messageBox = null!;
    private IUserSettingsService _userSettings = null!;
    private IThemeService _themeService = null!;
    private IAutoUpdateService _autoUpdate = null!;
    private IEventSubscription? _updateAvailableSubscription;
    private IEventSubscription? _recordingCompletedSubscription;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private UpdateCheckResult? _pendingUpdate;
    private string? _pendingRecordingBalloonPath;
    private WpfMenuItem? _recentRecordingsMenuItem;
    private readonly List<RecentRecordingItem> _recentRecordings = [];

    private const string AutomationOpenImagePathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OPEN_IMAGE_PATH";
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint VK_PRINTSCREEN = 0x2C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _keyboardProc; // keep delegate alive to prevent GC
    private IntPtr _keyboardHook = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable IDE1006 // P/Invoke struct fields must match Windows API names exactly
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
#pragma warning restore IDE1006

    protected override void OnStartup(StartupEventArgs e)
    {
        var automationLaunchOptions = AutomationLaunchOptions.Parse(e.Args);
        base.OnStartup(e);
        _isAutomationMode = automationLaunchOptions.IsAutomationMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnippingTool", "logs", "snipping-.log");

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
        _themeService.Apply(_userSettings.Current.Theme);
        if (!automationLaunchOptions.IsAutomationMode)
        {
            var eventAggregator = _host.Services.GetRequiredService<IEventAggregator>();
            _updateAvailableSubscription = eventAggregator.Subscribe<UpdateAvailableMessage>(HandleUpdateAvailable);
            _recordingCompletedSubscription = eventAggregator.Subscribe<RecordingCompletedMessage>(HandleRecordingCompleted);
            _autoUpdate = _host.Services.GetRequiredService<IAutoUpdateService>();
        }

        _logger.LogInformation("SnippingTool starting up");

        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (automationLaunchOptions.IsAutomationMode)
        {
            _logger.LogInformation("SnippingTool automation mode enabled");
            ShowAutomationWindow(automationLaunchOptions);
            return;
        }

        InitializeTrayIcon();
        InitializeRecentRecordingsMenu();
#if DEBUG
        AddDebugMenuItems();
#endif
        RegisterGlobalHotkey();
        _logger.LogInformation("Global hotkey (Print Screen) registered");
        _host.StartAsync().GetAwaiter().GetResult();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
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
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddTransient<IVideoWriterFactory, VideoWriterFactory>();
        services.AddTransient<IScreenRecordingService, ScreenRecordingService>();
        services.AddSingleton<IGifExportService, GifExportService>();
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddSingleton<IOcrService, WindowsOcrService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<RecordingAnnotationViewModel>();
        services.AddTransient<OverlayWindow>(sp => new OverlayWindow(
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
            sp.GetRequiredService<RecordingAnnotationViewModel>()));
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

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("SnippingTool shutting down");
        _updateAvailableSubscription?.Dispose();
        _recordingCompletedSubscription?.Dispose();
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _trayIcon?.Dispose();
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
        base.OnExit(e);
        Log.CloseAndFlush();
    }

    private void RegisterGlobalHotkey()
    {
        _keyboardProc = KeyboardHookCallback;
        using var process = Process.GetCurrentProcess();
        var hMod = GetModuleHandle(process.MainModule?.ModuleName);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            _logger?.LogWarning("Failed to register low-level keyboard hook (error {Code}); Print Screen hotkey will not work",
                Marshal.GetLastWin32Error());
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kb.vkCode == _userSettings.Current.RegionCaptureHotkey)
            {
                Dispatcher.InvokeAsync(StartSnip);
                return (IntPtr)1; // suppress — prevents Windows from handling Print Screen
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
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

    private void InitializeTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute)),
            ToolTipText = "Snipping Tool",
            ContextMenu = CreateTrayContextMenu(),
        };
        _trayIcon.TrayLeftMouseUp += TrayIcon_LeftClick;
        _trayIcon.TrayBalloonTipClicked += OnTrayBalloonClicked;
    }

    private WpfContextMenu CreateTrayContextMenu()
    {
        var contextMenu = new WpfContextMenu();
        contextMenu.Items.Add(CreateTrayMenuItem("New Snip", NewSnip_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("Open image...", OpenImage_Click));
        contextMenu.Items.Add(new WpfSeparator());
        contextMenu.Items.Add(CreateTrayMenuItem("Settings", Settings_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("Check for Updates", CheckForUpdates_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("About", About_Click));
        contextMenu.Items.Add(new WpfSeparator());
        contextMenu.Items.Add(CreateTrayMenuItem("Exit", Exit_Click));
        return contextMenu;
    }

    private static WpfMenuItem CreateTrayMenuItem(string header, RoutedEventHandler clickHandler)
    {
        var menuItem = new WpfMenuItem
        {
            Header = header,
        };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => StartSnip();
    private void NewSnip_Click(object sender, RoutedEventArgs e) => StartSnip();

    private void InitializeRecentRecordingsMenu()
    {
        if (_trayIcon?.ContextMenu is not { } contextMenu)
        {
            return;
        }

        _recentRecordingsMenuItem = new WpfMenuItem
        {
            Header = "Recent recordings",
        };

        contextMenu.Items.Insert(2, _recentRecordingsMenuItem);
        RebuildRecentRecordingsMenu();
    }

    private void RebuildRecentRecordingsMenu()
    {
        if (_recentRecordingsMenuItem is null)
        {
            return;
        }

        _recentRecordingsMenuItem.Items.Clear();

        if (_recentRecordings.Count == 0)
        {
            _recentRecordingsMenuItem.Items.Add(new WpfMenuItem
            {
                Header = "No recent recordings",
                IsEnabled = false,
            });
            return;
        }

        foreach (var recentRecording in _recentRecordings)
        {
            var recentRecordingItem = new WpfMenuItem
            {
                Header = $"{recentRecording.FileName} ({recentRecording.ElapsedText})",
            };
            recentRecordingItem.Items.Add(CreateRecentRecordingActionMenuItem("Open", OpenRecentRecording_Click, recentRecording));
            recentRecordingItem.Items.Add(CreateRecentRecordingActionMenuItem("Open folder", OpenRecentRecordingFolder_Click, recentRecording));
            recentRecordingItem.Items.Add(CreateRecentRecordingActionMenuItem("Export to GIF", ExportRecentRecordingGif_Click, recentRecording));
            _recentRecordingsMenuItem.Items.Add(recentRecordingItem);
        }
    }

    private static WpfMenuItem CreateRecentRecordingActionMenuItem(
        string header,
        RoutedEventHandler clickHandler,
        RecentRecordingItem recentRecording)
    {
        var menuItem = new WpfMenuItem
        {
            Header = header,
            Tag = recentRecording,
        };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(OpenImage, DispatcherPriority.ApplicationIdle);
    }

    private void OpenImage()
    {
        var selectedPath = _isAutomationMode
            ? Environment.GetEnvironmentVariable(AutomationOpenImagePathEnvironmentVariable)
            : null;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            var dialogService = _host.Services.GetRequiredService<IDialogService>();
            selectedPath = dialogService.PickOpenImageFile();
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        try
        {
            var imageFileService = _host.Services.GetRequiredService<IImageFileService>();
            var bitmap = imageFileService.LoadForAnnotation(selectedPath);
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

    private void Exit_Click(object sender, RoutedEventArgs e) => Current.Shutdown();

#if DEBUG
    private void AddDebugMenuItems()
    {
        if (_trayIcon?.ContextMenu is not { } contextMenu)
        {
            return;
        }

        var simulateUiErrorMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = "Simulate UI Error",
            InputGestureText = "Ctrl+Shift+F12"
        };
        simulateUiErrorMenuItem.Click += SimulateUiError_Click;

        contextMenu.Items.Insert(Math.Max(0, contextMenu.Items.Count - 1), simulateUiErrorMenuItem);
    }

    private void SimulateUiError_Click(object sender, RoutedEventArgs e)
    {
        _logger?.LogDebug("Simulating UI recovery smoke test from tray menu");
        throw new InvalidOperationException("Debug-only UI recovery smoke test.");
    }
#endif

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = (System.Windows.Controls.MenuItem)sender;
        menuItem.IsEnabled = false;

        try
        {
            var updateService = _host.Services.GetRequiredService<IUpdateService>();
            var result = await updateService.CheckForUpdates();

            if (!result.IsUpdateAvailable)
            {
                var current = _host.Services.GetRequiredService<IAppVersionService>().Current;
                _messageBox.ShowInformation(
                    $"You're already on the latest version (v{current.Major}.{current.Minor}.{current.Build}).",
                    "Check for Updates");
                return;
            }

            await _autoUpdate.ConfirmAndInstall(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Update check failed");
            _messageBox.ShowWarning(
                "Could not check for updates. Please check your internet connection and try again.",
                "Check for Updates");
        }
        finally
        {
            menuItem.IsEnabled = true;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettingsWindow();

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

    private void About_Click(object sender, RoutedEventArgs e) => ShowAboutWindow();

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
        OpenImage_Click(this, new RoutedEventArgs());
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

    private void StartSnip()
    {
        _logger?.LogDebug("Snip started");
        var delay = _host.Services.GetRequiredService<IUserSettingsService>().Current.CaptureDelaySeconds;
        if (delay > 0)
        {
            new CountdownWindow(delay, ShowSelectionOverlay).Show();
            return;
        }

        ShowSelectionOverlay();
    }

    private async void ShowSelectionOverlay()
    {
        var selection = await SelectionSession.SelectAsync(
            _host.Services.GetRequiredService<IScreenCaptureService>(),
            _host.Services.GetRequiredService<ILoggerFactory>());

        if (selection is null)
        {
            return;
        }

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.InitializeFromSelectionSession(selection);
        DpiAwarenessScope.RunPerMonitorV2(() => overlay.Show());
    }

    private void ShowUpdateBalloon(UpdateCheckResult result)
    {
        _pendingRecordingBalloonPath = null;
        var v = result.LatestVersion;
        _trayIcon?.ShowBalloonTip(
            "Update Available",
            $"Version {v.Major}.{v.Minor}.{v.Build} is ready to download.",
            BalloonIcon.Info);
    }

    private ValueTask HandleUpdateAvailable(UpdateAvailableMessage message)
    {
        _pendingUpdate = message.Result;
        ShowUpdateBalloon(message.Result);
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRecordingCompleted(RecordingCompletedMessage message)
    {
        var recentRecording = new RecentRecordingItem(message.OutputPath, message.ElapsedText);
        _recentRecordings.RemoveAll(item => string.Equals(item.OutputPath, recentRecording.OutputPath, StringComparison.OrdinalIgnoreCase));
        _recentRecordings.Insert(0, recentRecording);
        if (_recentRecordings.Count > 5)
        {
            _recentRecordings.RemoveRange(5, _recentRecordings.Count - 5);
        }

        RebuildRecentRecordingsMenu();
        ShowRecordingCompletedBalloon(recentRecording);
        return ValueTask.CompletedTask;
    }

    private void ShowRecordingCompletedBalloon(RecentRecordingItem recentRecording)
    {
        _pendingRecordingBalloonPath = recentRecording.OutputPath;
        var directory = Path.GetDirectoryName(recentRecording.OutputPath) ?? recentRecording.OutputPath;
        _trayIcon?.ShowBalloonTip(
            "Recording saved",
            $"{recentRecording.FileName} • {recentRecording.ElapsedText}{Environment.NewLine}{directory}",
            BalloonIcon.Info);
    }

    private async void OnTrayBalloonClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_pendingUpdate is not null)
        {
            var update = _pendingUpdate;
            _pendingUpdate = null;
            await _autoUpdate.ConfirmAndInstall(update);
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingRecordingBalloonPath))
        {
            return;
        }

        OpenPath(_pendingRecordingBalloonPath);
        _pendingRecordingBalloonPath = null;
    }

    private void OpenRecentRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: RecentRecordingItem recentRecording })
        {
            return;
        }

        OpenPath(recentRecording.OutputPath);
    }

    private void OpenRecentRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: RecentRecordingItem recentRecording })
        {
            return;
        }

        var directory = Path.GetDirectoryName(recentRecording.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenFolder(directory);
        }
    }

    private async void ExportRecentRecordingGif_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: RecentRecordingItem recentRecording } menuItem)
        {
            return;
        }

        if (!File.Exists(recentRecording.OutputPath))
        {
            _messageBox.ShowWarning("The recording file could not be found.", "Export to GIF");
            return;
        }

        var gifPath = Path.ChangeExtension(recentRecording.OutputPath, ".gif");
        menuItem.IsEnabled = false;

        try
        {
            var gifExportService = _host.Services.GetRequiredService<IGifExportService>();
            await gifExportService.Export(recentRecording.OutputPath, gifPath, _userSettings.Current.GifFps).ConfigureAwait(true);
            var directory = Path.GetDirectoryName(gifPath) ?? gifPath;
            _trayIcon?.ShowBalloonTip(
                "GIF exported",
                $"{Path.GetFileName(gifPath)} is ready.{Environment.NewLine}{directory}",
                BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GIF export from recent recordings failed for {Path}", recentRecording.OutputPath);
            _messageBox.ShowWarning("The GIF export failed. Please try again.", "Export to GIF");
        }
        finally
        {
            menuItem.IsEnabled = true;
        }
    }

    private void OpenPath(string path)
    {
        if (!File.Exists(path))
        {
            _messageBox.ShowWarning("The selected recording file could not be found.", "Open Recording");
            return;
        }

        var processService = _host.Services.GetRequiredService<IProcessService>();
        processService.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    private void OpenFolder(string path)
    {
        var processService = _host.Services.GetRequiredService<IProcessService>();
        processService.Start(new ProcessStartInfo("explorer.exe", path));
    }

    private sealed record RecentRecordingItem(string OutputPath, string ElapsedText)
    {
        public string FileName => Path.GetFileName(OutputPath);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled dispatcher exception");
        e.Handled = true;

        var closedWindowName = TryRecoverFromActiveWindow();
        var recoveryMessage = closedWindowName is null
            ? "You can continue using SnippingTool. Details have been written to the log file."
            : $"{closedWindowName} was closed so SnippingTool can recover. You can continue using the app. Details have been written to the log file.";

        _messageBox.ShowError(
            $"Something went wrong while processing your last action.\n\n{e.Exception.Message}\n\n{recoveryMessage}",
            "SnippingTool — Recovered From Error");
    }

    private string? TryRecoverFromActiveWindow()
    {
        try
        {
            var window = GetRecoveryWindow();
            if (window is null)
            {
                return null;
            }

            var windowName = string.IsNullOrWhiteSpace(window.Title)
                ? window.GetType().Name
                : window.Title;

            CloseWindowTree(window);
            _logger?.LogWarning(
                "Closed window {WindowType} during dispatcher exception recovery",
                window.GetType().Name);

            return windowName;
        }
        catch (Exception recoveryException)
        {
            _logger?.LogError(recoveryException, "Failed to recover active window after dispatcher exception");
            return null;
        }
    }

    private Window? GetRecoveryWindow()
    {
        var visibleWindows = Current.Windows
            .OfType<Window>()
            .Where(window => window.IsVisible)
            .ToList();

        return visibleWindows.FirstOrDefault(window => window.IsActive)
            ?? visibleWindows.FirstOrDefault(window => window is OverlayWindow
                or CountdownWindow
                or UpdateDownloadWindow
                or SettingsWindow
                or AboutWindow
                or PinnedScreenshotWindow)
            ?? visibleWindows.FirstOrDefault();
    }

    private static void CloseWindowTree(Window rootWindow)
    {
        foreach (var ownedWindow in rootWindow.OwnedWindows.OfType<Window>().ToList())
        {
            CloseWindowTree(ownedWindow);
        }

        if (rootWindow.IsVisible)
        {
            rootWindow.Close();
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception — application will terminate");
        Log.CloseAndFlush();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }
}
