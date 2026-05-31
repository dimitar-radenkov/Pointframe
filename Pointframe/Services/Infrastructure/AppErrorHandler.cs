using System.Windows;
using System.Windows.Threading;
using Serilog;
using WpfApplication = System.Windows.Application;

namespace Pointframe.Services;

internal sealed class AppErrorHandler : IAppErrorHandler
{
    private readonly ILogger<AppErrorHandler> _logger;
    private readonly IMessageBoxService _messageBox;
    private readonly ITelemetryService _telemetry;

    public AppErrorHandler(ILogger<AppErrorHandler> logger, IMessageBoxService messageBox, ITelemetryService telemetry)
    {
        _logger = logger;
        _messageBox = messageBox;
        _telemetry = telemetry;
    }

    public void Register()
    {
        WpfApplication.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled dispatcher exception");
        _telemetry.TrackException(e.Exception, "dispatcher");
        e.Handled = true;

        var closedWindowName = TryRecoverFromActiveWindow();
        var recoveryMessage = closedWindowName is null
            ? "You can continue using Pointframe. Details have been written to the log file."
            : $"{closedWindowName} was closed so Pointframe can recover. You can continue using the app. Details have been written to the log file.";

        _messageBox.ShowError(
            $"Something went wrong while processing your last action.\n\n{e.Exception.Message}\n\n{recoveryMessage}",
            "Pointframe — Recovered From Error");
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
            _logger.LogWarning(
                "Closed window {WindowType} during dispatcher exception recovery",
                window.GetType().Name);

            return windowName;
        }
        catch (Exception recoveryException)
        {
            _logger.LogError(recoveryException, "Failed to recover active window after dispatcher exception");
            return null;
        }
    }

    private static Window? GetRecoveryWindow()
    {
        var visibleWindows = WpfApplication.Current.Windows
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
        _logger.LogCritical(e.ExceptionObject as Exception, "Unhandled AppDomain exception — application will terminate");
        Log.CloseAndFlush();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unobserved task exception");
        _telemetry.TrackException(e.Exception, "task");
        e.SetObserved();
    }
}
