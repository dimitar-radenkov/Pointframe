using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SnippingTool.Models;
using SnippingTool.Services;
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
    private IAutoUpdateService _autoUpdate = null!;
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
        _autoUpdate = _host.Services.GetRequiredService<IAutoUpdateService>();
        _autoUpdate.UpdateAvailable += result =>
        {
            _pendingUpdate = result;
            ShowUpdateBalloon(result);
        };
        _logger.LogInformation("SnippingTool starting up");

        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _trayIcon = (TaskbarIcon)FindResource(TrayIconResourceKey);
        _trayIcon.TrayBalloonTipClicked += OnUpdateBalloonClicked;

        RegisterGlobalHotkey();
        _logger.LogInformation("Global hotkey (Print Screen) registered");
        _host.StartAsync().GetAwaiter().GetResult();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppVersionService, AppVersionService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddTransient<IVideoWriterFactory, VideoWriterFactory>();
        services.AddTransient<IScreenRecordingService, ScreenRecordingService>();
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddSingleton<IOcrService, WindowsOcrService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<OverlayWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
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
    private void Exit_Click(object sender, RoutedEventArgs e) => Current.Shutdown();

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = (System.Windows.Controls.MenuItem)sender;
        menuItem.IsEnabled = false;

        try
        {
            var updateService = _host.Services.GetRequiredService<IUpdateService>();
            var result = await updateService.CheckForUpdatesAsync();

            if (!result.IsUpdateAvailable)
            {
                var current = _host.Services.GetRequiredService<IAppVersionService>().Current;
                _messageBox.ShowInformation(
                    $"You're already on the latest version (v{current.Major}.{current.Minor}.{current.Build}).",
                    "Check for Updates");
                return;
            }

            await _autoUpdate.ConfirmAndInstallAsync(result);
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
            new CountdownWindow(delay, () => _host.Services.GetRequiredService<OverlayWindow>().Show()).Show();
            return;
        }

        _host.Services.GetRequiredService<OverlayWindow>().Show();
    }

    private void ShowUpdateBalloon(UpdateCheckResult result)
    {
        var v = result.LatestVersion;
        _trayIcon?.ShowBalloonTip(
            "Update Available",
            $"Version {v.Major}.{v.Minor}.{v.Build} is ready to download.",
            BalloonIcon.Info);
    }

    private async void OnUpdateBalloonClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        var update = _pendingUpdate;
        _pendingUpdate = null;
        await _autoUpdate.ConfirmAndInstallAsync(update);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled dispatcher exception");
        e.Handled = true;
        _messageBox.ShowError(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails have been written to the log file.",
            "SnippingTool — Unexpected Error");
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
