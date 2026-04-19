using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Pointframe.Models;
using Forms = System.Windows.Forms;

namespace Pointframe;

public partial class OverlayWindow
{
    private async void Record_Click(object sender, RoutedEventArgs e) => await StartRecordingSession();

    private async Task StartRecordingSession()
    {
        var selectionRect = _vm.SelectionRect;
        var captureBoundsPixels = _vm.SelectionScreenBoundsPixels.Width > 0 && _vm.SelectionScreenBoundsPixels.Height > 0
            ? _vm.SelectionScreenBoundsPixels
            : GetScreenPixelBounds(selectionRect);
        var monitorBounds = new System.Drawing.Rectangle(
            captureBoundsPixels.X,
            captureBoundsPixels.Y,
            captureBoundsPixels.Width,
            captureBoundsPixels.Height);
        var monitorName = Forms.Screen.FromRectangle(monitorBounds).DeviceName;

        _recordingSessionGeometry = CreateRecordingSessionGeometry(
            selectionRect,
            captureBoundsPixels,
            monitorName);

        var captureBounds = _recordingSessionGeometry.CaptureBoundsPixels;
        var screenX = captureBounds.X;
        var screenY = captureBounds.Y;
        var screenW = captureBounds.Width;
        var screenH = captureBounds.Height;

        _logger.LogDebug(
            "StartRecordingSession: selection DIP X={SelX} Y={SelY} W={SelW} H={SelH}",
            selectionRect.X, selectionRect.Y, selectionRect.Width, selectionRect.Height);
        _logger.LogDebug(
            "StartRecordingSession: capture physical px X={ScreenX} Y={ScreenY} W={ScreenW} H={ScreenH}",
            screenX, screenY, screenW, screenH);

        var videosDir = _userSettings.Current.RecordingOutputPath;
        _fileSystem.CreateDirectory(videosDir);
        var path = _fileSystem.CombinePath(videosDir, $"SnipRec-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        try
        {
            Visibility = Visibility.Hidden;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            await Task.Run(() => _recorder.Start(screenX, screenY, screenW, screenH, path));
        }
        catch (System.IO.FileNotFoundException ex)
        {
            Visibility = Visibility.Visible;
            _messageBox.ShowWarning(ex.Message, "ffmpeg not found");
            return;
        }

        if (_userSettings.Current.RecordMicrophone && !_recorder.IsRecordingMicrophoneEnabled)
        {
            _messageBox.ShowWarning(
                "Microphone recording is enabled, but no compatible microphone device was available. The recording will continue without microphone audio.",
                "Microphone unavailable");
        }

        RecordingOverlayWindow? recordingOverlay = null;
        DpiAwarenessScope.RunPerMonitorV2(() =>
        {
            recordingOverlay = new RecordingOverlayWindow(
                _recordingSessionGeometry,
                path,
                _recorder,
                _screenCapture,
                _mouseHookService,
                _recordingHudViewModelFactory,
                _eventAggregator,
                _loggerFactory,
                _userSettings,
                _recordingAnnotationViewModel);
        });

        if (recordingOverlay is null)
        {
            Visibility = Visibility.Visible;
            _logger.LogError("Failed to create the recording overlay window");
            return;
        }

        if (System.Windows.Application.Current is App app)
        {
            app.RegisterAutomationWindow(recordingOverlay);
        }

        _closeLeavesRecorderRunning = true;
        DpiAwarenessScope.RunPerMonitorV2(() => recordingOverlay.Show());
        Close();
    }

    private void CloseRecordingSessionWindows()
    {
        _recordingSessionGeometry = RecordingSessionGeometry.Empty;
    }

    internal static Rect CalculateRecordingBorderRect(Rect selectionRect, double borderOffset)
    {
        return new Rect(
            selectionRect.Left - borderOffset,
            selectionRect.Top - borderOffset,
            selectionRect.Width + (borderOffset * 2d),
            selectionRect.Height + (borderOffset * 2d));
    }

    internal static RecordingSessionGeometry CreateRecordingSessionGeometry(
        Rect selectionRect,
        Int32Rect captureBoundsPixels,
        string monitorName)
    {
        var monitorBounds = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
            captureBoundsPixels.X,
            captureBoundsPixels.Y,
            captureBoundsPixels.Width,
            captureBoundsPixels.Height)).Bounds;

        return CreateRecordingSessionGeometry(
            selectionRect,
            captureBoundsPixels,
            monitorName,
            new Int32Rect(
                monitorBounds.X,
                monitorBounds.Y,
                monitorBounds.Width,
                monitorBounds.Height),
            new Int32Rect(
                Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
                    captureBoundsPixels.X,
                    captureBoundsPixels.Y,
                    captureBoundsPixels.Width,
                    captureBoundsPixels.Height)).WorkingArea.X,
                Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
                    captureBoundsPixels.X,
                    captureBoundsPixels.Y,
                    captureBoundsPixels.Width,
                    captureBoundsPixels.Height)).WorkingArea.Y,
                Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
                    captureBoundsPixels.X,
                    captureBoundsPixels.Y,
                    captureBoundsPixels.Width,
                    captureBoundsPixels.Height)).WorkingArea.Width,
                Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
                    captureBoundsPixels.X,
                    captureBoundsPixels.Y,
                    captureBoundsPixels.Width,
                    captureBoundsPixels.Height)).WorkingArea.Height));
    }

    internal static RecordingSessionGeometry CreateRecordingSessionGeometry(
        Rect selectionRect,
        Int32Rect captureBoundsPixels,
        string monitorName,
        Int32Rect hostBoundsPixels,
        Int32Rect workAreaBoundsPixels)
    {
        var monitorScaleX = selectionRect.Width > 0d ? captureBoundsPixels.Width / selectionRect.Width : 1d;
        var monitorScaleY = selectionRect.Height > 0d ? captureBoundsPixels.Height / selectionRect.Height : 1d;
        var captureRectDips = new Rect(
            (captureBoundsPixels.X - hostBoundsPixels.X) / monitorScaleX,
            (captureBoundsPixels.Y - hostBoundsPixels.Y) / monitorScaleY,
            captureBoundsPixels.Width / monitorScaleX,
            captureBoundsPixels.Height / monitorScaleY);

        return new RecordingSessionGeometry(
            hostBoundsPixels,
            captureBoundsPixels,
            workAreaBoundsPixels,
            new Rect(0, 0, hostBoundsPixels.Width / monitorScaleX, hostBoundsPixels.Height / monitorScaleY),
            new Rect(
                (workAreaBoundsPixels.X - hostBoundsPixels.X) / monitorScaleX,
                (workAreaBoundsPixels.Y - hostBoundsPixels.Y) / monitorScaleY,
                workAreaBoundsPixels.Width / monitorScaleX,
                workAreaBoundsPixels.Height / monitorScaleY),
            captureRectDips,
            monitorName,
            monitorScaleX,
            monitorScaleY);
    }
}
