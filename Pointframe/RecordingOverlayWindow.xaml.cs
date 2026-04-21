using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Cursors = System.Windows.Input.Cursors;

namespace Pointframe;

public partial class RecordingOverlayWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const double RecordingBorderStrokeThickness = 2d;
    private const double RecordingBorderClearance = 6d;
    private const double RecordingBorderOffset = RecordingBorderStrokeThickness + RecordingBorderClearance;

    private readonly RecordingSessionGeometry _geometry;
    private readonly string _outputPath;
    private readonly IScreenRecordingService _recorder;
    private readonly IScreenCaptureService _screenCapture;
    private readonly Func<IScreenRecordingService, string, RecordingHudViewModel> _recordingHudViewModelFactory;
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<RecordingOverlayWindow> _logger;
    private readonly IUserSettingsService _userSettings;
    private readonly RecordingAnnotationViewModel _recordingAnnotationViewModel;
    private readonly AnnotationCanvasRenderer _recordingRenderer;
    private readonly AnnotationCanvasInteractionController _recordingInteractionController;
    private readonly RecordingAnnotationSurfaceCoordinator _recordingAnnotationSurfaceCoordinator;
    private readonly RecordingCursorEffectsService _recordingCursorEffectsService;
    private readonly RecordingHudCoordinator _recordingHudCoordinator;
    private readonly RecordingMousePassthroughCoordinator _recordingMousePassthroughCoordinator;
    private readonly IEventSubscription _recordingUndoSubscription;
    private readonly IEventSubscription _recordingRedoSubscription;
    private readonly Func<Point?> _getCursorScreenPoint;

    private HwndSource? _windowSource;

    internal RecordingOverlayWindow(
        RecordingSessionGeometry geometry,
        string outputPath,
        IScreenRecordingService recorder,
        IScreenCaptureService screenCapture,
        IMouseHookService mouseHookService,
        Func<IScreenRecordingService, string, RecordingHudViewModel> recordingHudViewModelFactory,
        IEventAggregator eventAggregator,
        ILoggerFactory loggerFactory,
        IUserSettingsService userSettings,
        RecordingAnnotationViewModel recordingAnnotationViewModel,
        Func<Point?>? getCursorScreenPoint = null)
    {
        _geometry = geometry;
        _outputPath = outputPath;
        _recorder = recorder;
        _screenCapture = screenCapture;
        _recordingHudViewModelFactory = recordingHudViewModelFactory;
        _eventAggregator = eventAggregator;
        _logger = loggerFactory.CreateLogger<RecordingOverlayWindow>();
        _userSettings = userSettings;
        _recordingAnnotationViewModel = recordingAnnotationViewModel;
        _getCursorScreenPoint = getCursorScreenPoint ?? RecordingOverlayNativeInterop.GetCursorScreenPoint;

        InitializeComponent();

        Width = _geometry.HostBoundsDips.Width;
        Height = _geometry.HostBoundsDips.Height;

        _recordingAnnotationSurfaceCoordinator = new RecordingAnnotationSurfaceCoordinator(
            RecordingAnnotationCanvas,
            _geometry,
            _recordingAnnotationViewModel);
        _recordingRenderer = new AnnotationCanvasRenderer(
            RecordingAnnotationCanvas,
            _recordingAnnotationViewModel,
            element => _recordingAnnotationViewModel.TrackElement(element),
            loggerFactory.CreateLogger<AnnotationCanvasRenderer>(),
            () => _recordingAnnotationSurfaceCoordinator.SyncAnnotationState(),
            CaptureLiveRecordingBlurSource);
        _recordingInteractionController = new AnnotationCanvasInteractionController(
            RecordingAnnotationCanvas,
            _recordingAnnotationViewModel,
            _recordingRenderer,
            () => _recordingAnnotationSurfaceCoordinator.SyncAnnotationState());
        _recordingCursorEffectsService = new RecordingCursorEffectsService(
            RecordingCursorEffectsCanvas,
            _geometry,
            mouseHookService,
            _userSettings,
            () => _recordingAnnotationViewModel.IsInputArmed,
            loggerFactory.CreateLogger<RecordingCursorEffectsService>());
        _recordingHudCoordinator = new RecordingHudCoordinator(
            RecordingHudPanel,
            _geometry,
            _userSettings,
            _logger);
        _recordingMousePassthroughCoordinator = new RecordingMousePassthroughCoordinator(
            () => _recordingAnnotationViewModel.IsInputArmed,
            _getCursorScreenPoint,
            IsPointInsideRecordingHud,
            SetWindowMouseTransparency,
            Dispatcher);
        _recordingUndoSubscription = _eventAggregator.Subscribe<UndoGroupMessage>(HandleRecordingUndoGroup);
        _recordingRedoSubscription = _eventAggregator.Subscribe<RedoGroupMessage>(HandleRecordingRedoGroup);
        _recordingAnnotationViewModel.ClearRequested += HandleRecordingClearRequested;

        RecordingAnnotationCanvas.MouseLeftButtonDown += RecordingAnnot_Down;
        RecordingAnnotationCanvas.MouseMove += RecordingAnnot_Move;
        RecordingAnnotationCanvas.MouseLeftButtonUp += RecordingAnnot_Up;
        KeyDown += Window_KeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(WndProc);

        LogSessionStartDiagnostics();
        PositionWindow();
        PositionRecordingBorder();
        _recordingCursorEffectsService.Start();
        _recordingMousePassthroughCoordinator.Start();
        InitializeRecordingAnnotationSurface();

        var hudViewModel = _recordingHudViewModelFactory(_recorder, _outputPath);
        hudViewModel.AttachAnnotationSession(_recordingAnnotationViewModel, ToggleRecordingAnnotationInput);
        ShowRecordingHud(hudViewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WndProc);
            _windowSource = null;
        }

        _recordingUndoSubscription.Dispose();
        _recordingRedoSubscription.Dispose();
        _recordingAnnotationViewModel.ClearRequested -= HandleRecordingClearRequested;
        _recordingCursorEffectsService.Dispose();
        _recordingMousePassthroughCoordinator.Dispose();
        HideRecordingHud();
        HideRecordingAnnotationSurface();

        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }

        base.OnClosed(e);
    }

    private void PositionWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        RecordingOverlayNativeInterop.MoveWindow(handle, _geometry.HostBoundsPixels);
        RecordingOverlayNativeInterop.MoveWindow(handle, _geometry.HostBoundsPixels);

        var actualRect = RecordingOverlayNativeInterop.TryGetWindowRect(handle);
        if (actualRect is not null)
        {
            _logger.LogDebug(
                "Recording overlay host positioned: requestedPx={RequestedX},{RequestedY},{RequestedW},{RequestedH} actualPx={ActualX},{ActualY},{ActualW},{ActualH}",
                _geometry.HostBoundsPixels.X,
                _geometry.HostBoundsPixels.Y,
                _geometry.HostBoundsPixels.Width,
                _geometry.HostBoundsPixels.Height,
                actualRect.Value.X,
                actualRect.Value.Y,
                actualRect.Value.Width,
                actualRect.Value.Height);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var screenPoint = GetScreenPointFromLParam(lParam);
        if (IsPointInsideRecordingHud(screenPoint) || IsPointInsideRecordingCaptureSurface(screenPoint))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private static Point GetScreenPointFromLParam(IntPtr lParam)
    {
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        return new Point(x, y);
    }

    private bool IsPointInsideRecordingHud(Point screenPoint)
    {
        if (!IsVisibleWithBounds(RecordingHudPanel))
        {
            return false;
        }

        var hudBounds = new Rect(
            Canvas.GetLeft(RecordingHudPanel),
            Canvas.GetTop(RecordingHudPanel),
            RecordingHudPanel.ActualWidth,
            RecordingHudPanel.ActualHeight);
        return _geometry.IsScreenPixelPointInsideHostRect(screenPoint, hudBounds);
    }

    private bool IsPointInsideRecordingCaptureSurface(Point screenPoint)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed || _geometry.IsEmpty)
        {
            return false;
        }

        return _geometry.IsScreenPixelPointInsideCapture(screenPoint);
    }

    private static bool HasBounds(FrameworkElement element)
    {
        return element.ActualWidth > 0
            && element.ActualHeight > 0;
    }

    private static bool IsVisibleWithBounds(FrameworkElement element)
    {
        return element.Visibility == Visibility.Visible
            && HasBounds(element);
    }

    private void PositionRecordingBorder()
    {
        var borderRect = _geometry.GetRecordingBorderRectDips(RecordingBorderOffset);

        Canvas.SetLeft(RecordingBorderWhite, borderRect.Left);
        Canvas.SetTop(RecordingBorderWhite, borderRect.Top);
        RecordingBorderWhite.Width = borderRect.Width;
        RecordingBorderWhite.Height = borderRect.Height;

        Canvas.SetLeft(RecordingBorderBlack, borderRect.Left);
        Canvas.SetTop(RecordingBorderBlack, borderRect.Top);
        RecordingBorderBlack.Width = borderRect.Width;
        RecordingBorderBlack.Height = borderRect.Height;
    }

    private void InitializeRecordingAnnotationSurface()
    {
        _recordingAnnotationSurfaceCoordinator.Initialize(
            GetRecordingAnnotationCursor(),
            PreWarmRecordingAnnotationRenderer,
            LogAnnotationSurfaceDiagnostics);
    }

    private void PreWarmRecordingAnnotationRenderer()
    {
        UIElement[] warmupElements =
        [
            new Polyline { Stroke = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false },
            new Line { Stroke = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false },
            new System.Windows.Shapes.Rectangle { Stroke = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false },
            new Polygon { Stroke = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false },
            new Ellipse { Stroke = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false },
        ];

        foreach (var element in warmupElements)
        {
            RecordingAnnotationCanvas.Children.Add(element);
        }

        foreach (var element in warmupElements)
        {
            RecordingAnnotationCanvas.Children.Remove(element);
        }
    }

    private void HideRecordingAnnotationSurface()
    {
        _recordingAnnotationSurfaceCoordinator.Hide(() => SetRecordingAnnotationInputArmed(false, force: true));
    }

    private bool ToggleRecordingAnnotationInput()
    {
        var isInputArmed = !_recordingAnnotationViewModel.IsInputArmed;
        SetRecordingAnnotationInputArmed(isInputArmed);
        return _recordingAnnotationViewModel.IsInputArmed;
    }

    private void SetRecordingAnnotationInputArmed(bool isInputArmed, bool force = false)
    {
        if (!force && _recordingAnnotationViewModel.IsInputArmed == isInputArmed)
        {
            return;
        }

        if (!isInputArmed && !force && _recordingAnnotationSurfaceCoordinator.HasActiveEditor())
        {
            _logger.LogInformation("Recording annotation input remains armed because an editor is active");
            return;
        }

        if (!isInputArmed)
        {
            _recordingInteractionController.Cancel();
        }

        _recordingAnnotationViewModel.SetInputArmed(isInputArmed);
        _recordingAnnotationSurfaceCoordinator.UpdateCursor(GetRecordingAnnotationCursor());
        _recordingMousePassthroughCoordinator.Update();

        if (_recordingAnnotationViewModel.IsInputArmed)
        {
            Activate();
            Focus();
        }
    }

    private System.Windows.Input.Cursor GetRecordingAnnotationCursor() => !_recordingAnnotationViewModel.IsInputArmed
        ? Cursors.Arrow
        : _recordingAnnotationViewModel.SelectedTool == AnnotationTool.Text
            ? Cursors.IBeam
            : Cursors.Pen;

    private void RecordingAnnot_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return;
        }

        _recordingInteractionController.HandlePointerDown(e.GetPosition(RecordingAnnotationCanvas));
        e.Handled = true;
    }

    private void RecordingAnnot_Move(object sender, MouseEventArgs e)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return;
        }

        _recordingInteractionController.HandlePointerMove(e.GetPosition(RecordingAnnotationCanvas));
    }

    private void RecordingAnnot_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return;
        }

        _recordingInteractionController.HandlePointerUp(e.GetPosition(RecordingAnnotationCanvas));
        e.Handled = true;
    }

    private void HandleRecordingClearRequested()
    {
        _recordingAnnotationSurfaceCoordinator.HandleClearRequested(_recordingInteractionController.Cancel);
    }

    private ValueTask HandleRecordingUndoGroup(UndoGroupMessage message)
    {
        _recordingAnnotationSurfaceCoordinator.ApplyUndo(message.Elements);
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRecordingRedoGroup(RedoGroupMessage message)
    {
        _recordingAnnotationSurfaceCoordinator.ApplyRedo(message.Elements);
        return ValueTask.CompletedTask;
    }

    private void SetWindowMouseTransparency(bool isTransparent)
    {
        if (_windowSource is null)
        {
            return;
        }

        var handle = _windowSource.Handle;
        RecordingOverlayNativeInterop.SetMouseTransparency(handle, isTransparent);
    }

    private BitmapSource? CaptureLiveRecordingBlurSource(BlurShapeParameters parameters)
    {
        var captureBounds = OverlayWindow.CalculateRecordingCaptureBounds(_geometry, parameters);

        var previousVisibility = Visibility;
        try
        {
            Visibility = Visibility.Hidden;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var bitmap = _screenCapture.Capture(captureBounds.X, captureBounds.Y, captureBounds.Width, captureBounds.Height);
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }
        finally
        {
            Visibility = previousVisibility;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }
    }

    private void ShowRecordingHud(RecordingHudViewModel hudViewModel)
    {
        _recordingHudCoordinator.Show(hudViewModel, OnRecordingHudCloseRequested);
        _recordingMousePassthroughCoordinator.Update();
    }

    private void HideRecordingHud()
    {
        _recordingHudCoordinator.Hide(OnRecordingHudCloseRequested);
        _recordingMousePassthroughCoordinator.Update();
    }

    private void OnRecordingHudCloseRequested()
    {
        Dispatcher.Invoke(Close);
    }

    private void RecordingHudPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RecordingHudPanel.Visibility == Visibility.Visible)
        {
            _recordingHudCoordinator.Position();
            _recordingMousePassthroughCoordinator.Update();
        }
    }

    private void PositionRecordingHud()
    {
        _recordingHudCoordinator.Position();
    }

    private void LogSessionStartDiagnostics()
    {
        _logger.LogInformation(
            "Recording overlay session start: monitor={MonitorName} hostPx={HostPxX},{HostPxY},{HostPxW},{HostPxH} capturePx={CapturePxX},{CapturePxY},{CapturePxW},{CapturePxH} workAreaPx={WorkPxX},{WorkPxY},{WorkPxW},{WorkPxH} hostDips={HostDipX},{HostDipY},{HostDipW},{HostDipH} captureDips={CaptureDipX},{CaptureDipY},{CaptureDipW},{CaptureDipH} workAreaDips={WorkDipX},{WorkDipY},{WorkDipW},{WorkDipH}",
            _geometry.MonitorName,
            _geometry.HostBoundsPixels.X,
            _geometry.HostBoundsPixels.Y,
            _geometry.HostBoundsPixels.Width,
            _geometry.HostBoundsPixels.Height,
            _geometry.CaptureBoundsPixels.X,
            _geometry.CaptureBoundsPixels.Y,
            _geometry.CaptureBoundsPixels.Width,
            _geometry.CaptureBoundsPixels.Height,
            _geometry.WorkAreaBoundsPixels.X,
            _geometry.WorkAreaBoundsPixels.Y,
            _geometry.WorkAreaBoundsPixels.Width,
            _geometry.WorkAreaBoundsPixels.Height,
            _geometry.HostBoundsDips.X,
            _geometry.HostBoundsDips.Y,
            _geometry.HostBoundsDips.Width,
            _geometry.HostBoundsDips.Height,
            _geometry.CaptureRectDips.X,
            _geometry.CaptureRectDips.Y,
            _geometry.CaptureRectDips.Width,
            _geometry.CaptureRectDips.Height,
            _geometry.WorkAreaBoundsDips.X,
            _geometry.WorkAreaBoundsDips.Y,
            _geometry.WorkAreaBoundsDips.Width,
            _geometry.WorkAreaBoundsDips.Height);
    }

    private void LogAnnotationSurfaceDiagnostics(Rect captureCanvasRect)
    {
        var captureCanvasPixels = _geometry.MapHostDipRectToScreenPixels(captureCanvasRect);
        _logger.LogDebug(
            "Recording overlay annotation surface mapped: canvasDips={CanvasX},{CanvasY},{CanvasW},{CanvasH} canvasPx={CanvasPxX},{CanvasPxY},{CanvasPxW},{CanvasPxH}",
            captureCanvasRect.X,
            captureCanvasRect.Y,
            captureCanvasRect.Width,
            captureCanvasRect.Height,
            captureCanvasPixels.X,
            captureCanvasPixels.Y,
            captureCanvasPixels.Width,
            captureCanvasPixels.Height);
    }

    private void RecordingToolButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag }
            && _recordingHudCoordinator is not null
            && _recordingHudCoordinator.TrySelectTool(tag))
        {
            _recordingAnnotationSurfaceCoordinator.UpdateCursor(GetRecordingAnnotationCursor());
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _recordingAnnotationViewModel.IsInputArmed:
                SetRecordingAnnotationInputArmed(false);
                e.Handled = true;
                break;
            case Key.Z when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                             && _recordingAnnotationViewModel.UndoCommand.CanExecute(null):
                _recordingAnnotationViewModel.UndoCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
