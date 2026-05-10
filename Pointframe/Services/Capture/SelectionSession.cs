using Microsoft.Extensions.Logging;
using Pointframe.Services;
using Forms = System.Windows.Forms;

namespace Pointframe;

internal static class SelectionSession
{
    internal static Task<SelectionSessionResult?> SelectAsync(
        IScreenCaptureService screenCapture,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SnippingTool.SelectionSession");
        var completionSource = new TaskCompletionSource<SelectionSessionResult?>();
        var windows = new List<SelectionMonitorWindow>();
        var completed = false;

        void CloseWindows()
        {
            foreach (var window in windows.ToList())
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
            }

            windows.Clear();
        }

        void Complete(SelectionSessionResult? result)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            CloseWindows();
            completionSource.TrySetResult(result);
        }

        foreach (var screen in Forms.Screen.AllScreens)
        {
            var monitorScale = MonitorDpiHelper.GetMonitorScale(screen.Bounds.Location);
            var hostBoundsDips = MonitorDpiHelper.CalculateWindowBounds(screen.Bounds, monitorScale);
            var hostBoundsPixels = new System.Windows.Int32Rect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            var monitorSnapshot = screenCapture.Capture(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            var window = new SelectionMonitorWindow(
                screen.DeviceName,
                monitorSnapshot,
                hostBoundsDips,
                hostBoundsPixels,
                monitorScale,
                monitorScale,
                loggerFactory.CreateLogger<SelectionMonitorWindow>());

            window.SelectionCompleted += result =>
            {
                logger.LogDebug(
                    "Selection monitor completed: monitor={Monitor} hostPx={HostX},{HostY},{HostW},{HostH} selectionPx={SelX},{SelY},{SelW},{SelH}",
                    result.MonitorName,
                    result.HostBoundsPixels.X,
                    result.HostBoundsPixels.Y,
                    result.HostBoundsPixels.Width,
                    result.HostBoundsPixels.Height,
                    result.SelectionBoundsPixels.X,
                    result.SelectionBoundsPixels.Y,
                    result.SelectionBoundsPixels.Width,
                    result.SelectionBoundsPixels.Height);
                Complete(result);
            };
            window.SelectionCanceled += () => Complete(null);
            windows.Add(window);
        }

        foreach (var window in windows)
        {
            DpiAwarenessScope.RunPerMonitorV2(() => window.Show());
        }

        return completionSource.Task;
    }

    internal static Task<SelectionSessionResult?> SelectWholeScreenAsync(
        IScreenCaptureService screenCapture,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SnippingTool.SelectionSession");
        var targetScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var monitorScale = MonitorDpiHelper.GetMonitorScale(targetScreen.Bounds.Location);
        var hostBoundsDips = MonitorDpiHelper.CalculateWindowBounds(targetScreen.Bounds, monitorScale);
        var hostBoundsPixels = new System.Windows.Int32Rect(
            targetScreen.Bounds.X,
            targetScreen.Bounds.Y,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height);
        var monitorSnapshot = screenCapture.Capture(
            targetScreen.Bounds.X,
            targetScreen.Bounds.Y,
            targetScreen.Bounds.Width,
            targetScreen.Bounds.Height);
        var selection = CreateWholeScreenSelectionResult(
            targetScreen.DeviceName,
            monitorSnapshot,
            hostBoundsDips,
            hostBoundsPixels,
            monitorScale,
            monitorScale);

        logger.LogDebug(
            "Whole-screen selection created: monitor={Monitor} hostPx={HostX},{HostY},{HostW},{HostH}",
            selection.MonitorName,
            selection.HostBoundsPixels.X,
            selection.HostBoundsPixels.Y,
            selection.HostBoundsPixels.Width,
            selection.HostBoundsPixels.Height);

        return Task.FromResult<SelectionSessionResult?>(selection);
    }

    internal static SelectionSessionResult CreateWholeScreenSelectionResult(
        string monitorName,
        System.Windows.Media.Imaging.BitmapSource monitorSnapshot,
        System.Windows.Rect hostBoundsDips,
        System.Windows.Int32Rect hostBoundsPixels,
        double dpiScaleX,
        double dpiScaleY)
    {
        return new SelectionSessionResult(
            monitorName,
            monitorSnapshot,
            monitorSnapshot,
            hostBoundsDips,
            hostBoundsPixels,
            new System.Windows.Rect(0d, 0d, hostBoundsDips.Width, hostBoundsDips.Height),
            hostBoundsPixels,
            dpiScaleX,
            dpiScaleY,
            SelectionSessionMode.FullScreen);
    }
}
