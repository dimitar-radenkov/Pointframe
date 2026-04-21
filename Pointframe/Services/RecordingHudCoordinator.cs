using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Pointframe.Models;
using Pointframe.ViewModels;

namespace Pointframe.Services;

internal sealed class RecordingHudCoordinator
{
    private readonly Border _recordingHudPanel;
    private readonly RecordingSessionGeometry _geometry;
    private readonly IUserSettingsService _userSettings;
    private readonly ILogger _logger;

    private RecordingHudViewModel? _recordingHudViewModel;
    private bool _initialHudDiagnosticsLogged;

    public RecordingHudCoordinator(
        Border recordingHudPanel,
        RecordingSessionGeometry geometry,
        IUserSettingsService userSettings,
        ILogger logger)
    {
        _recordingHudPanel = recordingHudPanel;
        _geometry = geometry;
        _userSettings = userSettings;
        _logger = logger;
    }

    public void Show(RecordingHudViewModel hudViewModel, Action closeRequestedHandler)
    {
        Hide(closeRequestedHandler);
        _recordingHudViewModel = hudViewModel;
        _recordingHudViewModel.CloseRequested += closeRequestedHandler;
        _recordingHudViewModel.StartElapsedTimer();
        _recordingHudPanel.DataContext = hudViewModel;
        _recordingHudPanel.Visibility = Visibility.Visible;
        _recordingHudPanel.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Position));
    }

    public void Hide(Action closeRequestedHandler)
    {
        if (_recordingHudViewModel is not null)
        {
            _recordingHudViewModel.CloseRequested -= closeRequestedHandler;
            _recordingHudViewModel.CancelElapsedTimer();
            _recordingHudViewModel = null;
        }

        _recordingHudPanel.DataContext = null;
        _recordingHudPanel.Visibility = Visibility.Collapsed;
    }

    public void Position()
    {
        if (_recordingHudPanel.Visibility != Visibility.Visible
            || _recordingHudPanel.ActualWidth <= 0
            || _recordingHudPanel.ActualHeight <= 0)
        {
            return;
        }

        var (left, top) = OverlayWindow.ComputeRecordingHudPosition(
            _geometry.CaptureRectDips,
            _recordingHudPanel.ActualWidth,
            _recordingHudPanel.ActualHeight,
            _geometry.WorkAreaBoundsDips,
            _userSettings.Current.HudGapPixels);
        Canvas.SetLeft(_recordingHudPanel, left);
        Canvas.SetTop(_recordingHudPanel, top);

        if (!_initialHudDiagnosticsLogged)
        {
            var hudBounds = new Rect(left, top, _recordingHudPanel.ActualWidth, _recordingHudPanel.ActualHeight);
            var hudBoundsPixels = _geometry.MapHostDipRectToScreenPixels(hudBounds);
            _logger.LogDebug(
                "Recording overlay HUD positioned: hudDips={HudX},{HudY},{HudW},{HudH} hudPx={HudPxX},{HudPxY},{HudPxW},{HudPxH} workAreaDips={WorkX},{WorkY},{WorkW},{WorkH}",
                hudBounds.X,
                hudBounds.Y,
                hudBounds.Width,
                hudBounds.Height,
                hudBoundsPixels.X,
                hudBoundsPixels.Y,
                hudBoundsPixels.Width,
                hudBoundsPixels.Height,
                _geometry.WorkAreaBoundsDips.X,
                _geometry.WorkAreaBoundsDips.Y,
                _geometry.WorkAreaBoundsDips.Width,
                _geometry.WorkAreaBoundsDips.Height);
            _initialHudDiagnosticsLogged = true;
        }
    }

    public bool TrySelectTool(string tag)
    {
        if (_recordingHudViewModel?.SelectToolCommand.CanExecute(tag) != true)
        {
            return false;
        }

        _recordingHudViewModel.SelectToolCommand.Execute(tag);
        return true;
    }
}
