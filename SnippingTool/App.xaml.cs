using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.DependencyInjection;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Application = System.Windows.Application;

namespace SnippingTool;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private HwndSource? _hotkeySource;
    private ServiceProvider _services = null!;

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

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        RegisterGlobalHotkey();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IScreenCaptureService, ScreenCaptureService>();
        services.AddSingleton<IAnnotationGeometryService, AnnotationGeometryService>();
        services.AddTransient<OverlayViewModel>();
        services.AddTransient<PreviewViewModel>();
        services.AddTransient<OverlayWindow>();
        services.AddTransient<Func<BitmapSource, Rect, PreviewWindow>>(
            sp => (bitmap, rect) => new PreviewWindow(sp.GetRequiredService<PreviewViewModel>(), bitmap, rect));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeySource != null)
        {
            UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            _hotkeySource.Dispose();
        }

        _trayIcon?.Dispose();
        _services.Dispose();
        base.OnExit(e);
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
        _services.GetRequiredService<OverlayWindow>().Show();
    }

    private void CaptureFullScreen()
    {
        var capture = _services.GetRequiredService<IScreenCaptureService>();
        var b = System.Windows.Forms.SystemInformation.VirtualScreen;
        OnSnipCompleted(capture.Capture(b.X, b.Y, b.Width, b.Height), System.Windows.Rect.Empty);
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

        var capture = _services.GetRequiredService<IScreenCaptureService>();
        OnSnipCompleted(capture.Capture(r.Left, r.Top, w, h), System.Windows.Rect.Empty);
    }

    private void OnSnipCompleted(BitmapSource bitmap, System.Windows.Rect snipScreenRect)
    {
        var factory = _services.GetRequiredService<Func<BitmapSource, Rect, PreviewWindow>>();
        factory(bitmap, snipScreenRect).Show();
    }
}