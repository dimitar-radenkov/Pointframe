using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private ServiceProvider _services = null!;
    private ILogger<App>? _logger;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const uint VK_PRINTSCREEN = 0x2C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _keyboardProc; // keep delegate alive to prevent GC
    private IntPtr _keyboardHook = IntPtr.Zero;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

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
        ConfigureServices(services, config);
        _services = services.BuildServiceProvider();

        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("SnippingTool starting up");

        Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        RegisterGlobalHotkey();
        _logger.LogInformation("Global hotkey (Print Screen) registered");
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.Configure<RecordingOptions>(config.GetSection(RecordingOptions.Section));
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddTransient<IScreenRecordingService, ScreenRecordingService>();
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<OverlayWindow>();
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
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception \u2014 application will terminate");
    }
}
