using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;
using Application = System.Windows.Application;

namespace SnippingTool;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private IHost _host = null!;
    private ILogger<App>? _logger;
    private IMessageBoxService _messageBox = null!;
    private IUserSettingsService _userSettings = null!;
    private IThemeService _themeService = null!;
    private IAutoUpdateService _autoUpdate = null!;
    private IEventSubscription? _updateAvailableSubscription;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private UpdateCheckResult? _pendingUpdate;

    private const string TrayIconResourceKey = "TrayIcon";
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
        base.OnStartup(e);
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
        var eventAggregator = _host.Services.GetRequiredService<IEventAggregator>();
        _updateAvailableSubscription = eventAggregator.Subscribe<UpdateAvailableMessage>(HandleUpdateAvailable);
        _autoUpdate = _host.Services.GetRequiredService<IAutoUpdateService>();
        _logger.LogInformation("SnippingTool starting up");

        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _trayIcon = (TaskbarIcon)FindResource(TrayIconResourceKey);
        _trayIcon.TrayBalloonTipClicked += OnUpdateBalloonClicked;
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
                sp.GetRequiredService<IUserSettingsService>(),
                sp.GetRequiredService<IProcessService>(),
                sp.GetRequiredService<IGifExportService>(),
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

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => StartSnip();
    private void NewSnip_Click(object sender, RoutedEventArgs e) => StartSnip();

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(OpenImage, DispatcherPriority.ApplicationIdle);
    }

    private void OpenImage()
    {
        var dialogService = _host.Services.GetRequiredService<IDialogService>();
        var selectedPath = dialogService.PickOpenImageFile();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        try
        {
            var imageFileService = _host.Services.GetRequiredService<IImageFileService>();
            var bitmap = imageFileService.LoadForAnnotation(selectedPath);
            var overlay = _host.Services.GetRequiredService<OverlayWindow>();
            overlay.InitializeFromImage(bitmap, selectedPath);
            overlay.Show();
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _host.Services.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = _host.Services.GetRequiredService<AboutWindow>();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
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

    private async void OnUpdateBalloonClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        var update = _pendingUpdate;
        _pendingUpdate = null;
        await _autoUpdate.ConfirmAndInstall(update);
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
