using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pointframe.ViewModels;

namespace Pointframe.Services;

internal sealed class RecordingHudCoordinator
{
    private readonly Border _expandedRecordingHudPanel;
    private readonly Border _compactRecordingHudPanel;
    private readonly RecordingSessionGeometry _geometry;
    private readonly IUserSettingsService _userSettings;
    private readonly ILogger _logger;
    private readonly Action _layoutChanged;

    private RecordingHudViewModel? _recordingHudViewModel;
    private bool _initialHudDiagnosticsLogged;

    public RecordingHudCoordinator(
        Border expandedRecordingHudPanel,
        Border compactRecordingHudPanel,
        RecordingSessionGeometry geometry,
        IUserSettingsService userSettings,
        ILogger logger,
        Action layoutChanged)
    {
        _expandedRecordingHudPanel = expandedRecordingHudPanel;
        _compactRecordingHudPanel = compactRecordingHudPanel;
        _geometry = geometry;
        _userSettings = userSettings;
        _logger = logger;
        _layoutChanged = layoutChanged;
    }

    public void Show(RecordingHudViewModel hudViewModel, Action closeRequestedHandler)
    {
        Hide(closeRequestedHandler);
        _recordingHudViewModel = hudViewModel;
        _recordingHudViewModel.CloseRequested += closeRequestedHandler;
        _recordingHudViewModel.PropertyChanged += HandleHudViewModelPropertyChanged;
        _recordingHudViewModel.StartElapsedTimer();
        _expandedRecordingHudPanel.DataContext = hudViewModel;
        _compactRecordingHudPanel.DataContext = hudViewModel;
        SchedulePosition();
    }

    public void Hide(Action closeRequestedHandler)
    {
        if (_recordingHudViewModel is not null)
        {
            _recordingHudViewModel.CloseRequested -= closeRequestedHandler;
            _recordingHudViewModel.PropertyChanged -= HandleHudViewModelPropertyChanged;
            _recordingHudViewModel.CancelElapsedTimer();
            _recordingHudViewModel = null;
        }

        _expandedRecordingHudPanel.DataContext = null;
        _compactRecordingHudPanel.DataContext = null;
        _layoutChanged();
    }

    public void Position()
    {
        var activeHudPanel = GetActiveHudPanel();
        if (activeHudPanel is null
            || activeHudPanel.ActualWidth <= 0
            || activeHudPanel.ActualHeight <= 0)
        {
            return;
        }

        var (left, top) = OverlayWindow.ComputeRecordingHudPosition(
            _geometry.CaptureRectDips,
            activeHudPanel.ActualWidth,
            activeHudPanel.ActualHeight,
            _geometry.WorkAreaBoundsDips,
            _geometry.IsFullScreenCapture,
            _userSettings.Current.HudGapPixels);
        Canvas.SetLeft(activeHudPanel, left);
        Canvas.SetTop(activeHudPanel, top);

        if (!_initialHudDiagnosticsLogged)
        {
            var hudBounds = new Rect(left, top, activeHudPanel.ActualWidth, activeHudPanel.ActualHeight);
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

        _layoutChanged();
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

    private void HandleHudViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordingHudViewModel.IsCompactMode)
            && e.PropertyName != nameof(RecordingHudViewModel.IsExpandedMode))
        {
            return;
        }

        SchedulePosition();
    }

    private void SchedulePosition()
    {
        _expandedRecordingHudPanel.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Position));
    }

    private Border? GetActiveHudPanel()
    {
        if (_compactRecordingHudPanel.Visibility == Visibility.Visible)
        {
            return _compactRecordingHudPanel;
        }

        if (_expandedRecordingHudPanel.Visibility == Visibility.Visible)
        {
            return _expandedRecordingHudPanel;
        }

        return null;
    }
}
