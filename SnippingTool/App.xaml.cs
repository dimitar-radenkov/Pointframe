using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    private HwndSource? _hotkeySource;
    private ServiceProvider _services = null!;
    private ILogger<App>? _logger;

    private const int HotkeyId = 9000;
    private const uint VK_PRINTSCREEN = 0x2C;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
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
        if (_hotkeySource != null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            _hotkeySource.Dispose();
        }

        _trayIcon?.Dispose();
        _services.Dispose();
        base.OnExit(e);
        Log.CloseAndFlush();
    }

    private void RegisterGlobalHotkey()
    {
        var p = new HwndSourceParameters("SnippingToolHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP
        };
        _hotkeySource = new HwndSource(p);
        _hotkeySource.AddHook(HotkeyHook);
        RegisterHotKey(_hotkeySource.Handle, HotkeyId, 0, VK_PRINTSCREEN);
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            StartSnip();
            handled = true;
        }

        return IntPtr.Zero;
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
