using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Application = System.Windows.Application;

namespace SnippingTool;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private ServiceProvider _services = null!;
    private ILogger<App>? _logger;
    private SettingsWindow? _settingsWindow;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint VK_PRINTSCREEN = 0x2C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _keyboardProc; // keep delegate alive to prevent GC
    private IntPtr _keyboardHook = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("SnippingTool starting up");

        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        RegisterGlobalHotkey();
        _logger.LogInformation("Global hotkey (Print Screen) registered");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddTransient<IScreenRecordingService, ScreenRecordingService>();
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<OverlayWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddSingleton<IUpdateService, GitHubUpdateService>();
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
        _services.Dispose();
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
            if (kb.vkCode == VK_PRINTSCREEN)
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
            var updateService = _services.GetRequiredService<IUpdateService>();
            var result = await updateService.CheckForUpdatesAsync();

            if (!result.IsUpdateAvailable)
            {
                var current = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
                System.Windows.MessageBox.Show(
                    $"You're already on the latest version (v{current.Major}.{current.Minor}.{current.Build}).",
                    "Check for Updates",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var v = result.LatestVersion;
            var answer = System.Windows.MessageBox.Show(
                $"Version {v.Major}.{v.Minor}.{v.Build} is available. Download and install now?",
                "Update Available",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            var fileName = $"SnippingTool-Setup-{v.Major}.{v.Minor}.{v.Build}.exe";
            var destPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "SnippingTool");
            var bytes = await http.GetByteArrayAsync(result.DownloadUrl);
            await System.IO.File.WriteAllBytesAsync(destPath, bytes);

            Process.Start(new ProcessStartInfo(destPath) { UseShellExecute = true });
            Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Update check failed");
            System.Windows.MessageBox.Show(
                "Could not check for updates. Please check your internet connection and try again.",
                "Check for Updates",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
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

        _settingsWindow = _services.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        // Brief delay so the context menu closes before we capture
        System.Threading.Tasks.Task.Delay(250).ContinueWith(
            _ => Dispatcher.Invoke(CaptureFullScreen));
    }

    private void ActiveWindow_Click(object sender, RoutedEventArgs e)
    {
        // Capture the foreground window before the menu disappears
        var hwnd = GetForegroundWindow();
        System.Threading.Tasks.Task.Delay(350).ContinueWith(
            _ => Dispatcher.Invoke(() => CaptureWindow(hwnd)));
    }

    private void StartSnip()
    {
        _logger?.LogDebug("Snip started");
        var delay = _services.GetRequiredService<IUserSettingsService>().Current.CaptureDelaySeconds;
        if (delay > 0)
        {
            new CountdownWindow(delay, () => _services.GetRequiredService<OverlayWindow>().Show()).Show();
            return;
        }

        _services.GetRequiredService<OverlayWindow>().Show();
    }

    private void CaptureFullScreen()
    {
        _logger?.LogInformation("Full screen capture initiated");
        var capture = _services.GetRequiredService<IScreenCaptureService>();
        var b = System.Windows.Forms.SystemInformation.VirtualScreen;
        System.Windows.Clipboard.SetImage(capture.Capture(b.X, b.Y, b.Width, b.Height));
    }

    private void CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
        {
            return;
        }

        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        _logger?.LogInformation("Window capture: {W}\u00d7{H}", w, h);
        var capture = _services.GetRequiredService<IScreenCaptureService>();
        System.Windows.Clipboard.SetImage(capture.Capture(r.Left, r.Top, w, h));
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled dispatcher exception");
        e.Handled = true;
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails have been written to the log file.",
            "SnippingTool — Unexpected Error",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
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
