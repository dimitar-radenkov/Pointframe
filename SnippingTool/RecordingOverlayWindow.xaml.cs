using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;
using Cursors = System.Windows.Input.Cursors;

namespace SnippingTool;

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
    private readonly RecordingCursorEffectsService _recordingCursorEffectsService;
    private readonly IEventSubscription _recordingUndoSubscription;
    private readonly IEventSubscription _recordingRedoSubscription;

    private HwndSource? _windowSource;
    private RecordingHudViewModel? _recordingHudViewModel;
    private bool _initialHudDiagnosticsLogged;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
        RecordingAnnotationViewModel recordingAnnotationViewModel)
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

        InitializeComponent();

        Width = _geometry.HostBoundsDips.Width;
        Height = _geometry.HostBoundsDips.Height;

        _recordingRenderer = new AnnotationCanvasRenderer(
            RecordingAnnotationCanvas,
            _recordingAnnotationViewModel,
            element => _recordingAnnotationViewModel.TrackElement(element),
            loggerFactory.CreateLogger<AnnotationCanvasRenderer>(),
            UpdateRecordingAnnotationStateFromCanvas,
            CaptureLiveRecordingBlurSource);
        _recordingInteractionController = new AnnotationCanvasInteractionController(
            RecordingAnnotationCanvas,
            _recordingAnnotationViewModel,
            _recordingRenderer,
            UpdateRecordingAnnotationStateFromCanvas);
        _recordingCursorEffectsService = new RecordingCursorEffectsService(
            RecordingCursorEffectsCanvas,
            _geometry,
            mouseHookService,
            _userSettings,
            () => _recordingAnnotationViewModel.IsInputArmed,
            loggerFactory.CreateLogger<RecordingCursorEffectsService>());
        _recordingUndoSubscription = _eventAggregator.Subscribe<UndoGroupMessage>(HandleRecordingUndoGroup);
        _recordingRedoSubscription = _eventAggregator.Subscribe<RedoGroupMessage>(HandleRecordingRedoGroup);
        _recordingAnnotationViewModel.ClearRequested += HandleRecordingClearRequested;

        RecordingAnnotationCanvas.MouseLeftButtonDown += RecordingAnnot_Down;
        RecordingAnnotationCanvas.MouseMove += RecordingAnnot_Move;
        RecordingAnnotationCanvas.MouseLeftButtonUp += RecordingAnnot_Up;
        KeyDown += Window_KeyDown;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(WndProc);

        LogSessionStartDiagnostics();
        PositionWindow();
        PositionRecordingBorder();
        _recordingCursorEffectsService.Start();
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
        MoveWindow(
            handle,
            _geometry.HostBoundsPixels.X,
            _geometry.HostBoundsPixels.Y,
            _geometry.HostBoundsPixels.Width,
            _geometry.HostBoundsPixels.Height,
            true);
        MoveWindow(
            handle,
            _geometry.HostBoundsPixels.X,
            _geometry.HostBoundsPixels.Y,
            _geometry.HostBoundsPixels.Width,
            _geometry.HostBoundsPixels.Height,
            true);

        if (GetWindowRect(handle, out var actualRect))
        {
            _logger.LogDebug(
                "Recording overlay host positioned: requestedPx={RequestedX},{RequestedY},{RequestedW},{RequestedH} actualPx={ActualX},{ActualY},{ActualW},{ActualH}",
                _geometry.HostBoundsPixels.X,
                _geometry.HostBoundsPixels.Y,
                _geometry.HostBoundsPixels.Width,
                _geometry.HostBoundsPixels.Height,
                actualRect.Left,
                actualRect.Top,
                actualRect.Right - actualRect.Left,
                actualRect.Bottom - actualRect.Top);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        var screenPoint = new Point(x, y);
        if (IsPointInsideRecordingHud(screenPoint) || IsPointInsideRecordingAnnotationCanvas(screenPoint))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private bool IsPointInsideRecordingHud(Point screenPoint)
    {
        if (RecordingHudPanel.Visibility != Visibility.Visible
            || RecordingHudPanel.ActualWidth <= 0
            || RecordingHudPanel.ActualHeight <= 0)
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

    private bool IsPointInsideRecordingAnnotationCanvas(Point screenPoint)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed
            || RecordingAnnotationCanvas.ActualWidth <= 0
            || RecordingAnnotationCanvas.ActualHeight <= 0)
        {
            return false;
        }

        return _geometry.IsScreenPixelPointInsideCapture(screenPoint);
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
        var captureCanvasRect = _geometry.GetCaptureCanvasRectDips();

        RecordingAnnotationCanvas.Width = captureCanvasRect.Width;
        RecordingAnnotationCanvas.Height = captureCanvasRect.Height;
        Canvas.SetLeft(RecordingAnnotationCanvas, captureCanvasRect.X);
        Canvas.SetTop(RecordingAnnotationCanvas, captureCanvasRect.Y);
        RecordingAnnotationCanvas.Cursor = GetRecordingAnnotationCursor();
        UpdateRecordingAnnotationStateFromCanvas();
        LogAnnotationSurfaceDiagnostics(captureCanvasRect);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, PreWarmRecordingAnnotationRenderer);
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
        if (_recordingAnnotationViewModel.ClearCommand.CanExecute(null))
        {
            _recordingAnnotationViewModel.ClearCommand.Execute(null);
        }

        SetRecordingAnnotationInputArmed(false, force: true);
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

        if (!isInputArmed && !force && HasActiveRecordingEditor())
        {
            _logger.LogInformation("Recording annotation input remains armed because an editor is active");
            return;
        }

        if (!isInputArmed)
        {
            _recordingInteractionController.Cancel();
        }

        _recordingAnnotationViewModel.SetInputArmed(isInputArmed);
        RecordingAnnotationCanvas.Cursor = GetRecordingAnnotationCursor();

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
        _recordingInteractionController.Cancel();
        RecordingAnnotationCanvas.Children.Clear();
        UpdateRecordingAnnotationStateFromCanvas();
    }

    private ValueTask HandleRecordingUndoGroup(UndoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            RecordingAnnotationCanvas.Children.Remove(element);
        }

        UpdateRecordingAnnotationStateFromCanvas();
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRecordingRedoGroup(RedoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            RecordingAnnotationCanvas.Children.Add(element);
        }

        UpdateRecordingAnnotationStateFromCanvas();
        return ValueTask.CompletedTask;
    }

    private void UpdateRecordingAnnotationStateFromCanvas()
    {
        var numberCount = RecordingAnnotationCanvas.Children
            .OfType<FrameworkElement>()
            .Count(element => element.Tag is "number");
        _recordingAnnotationViewModel.SyncAnnotationState(RecordingAnnotationCanvas.Children.Count > 0, numberCount);
    }

    private bool HasActiveRecordingEditor() => RecordingAnnotationCanvas.Children.OfType<TextBox>().Any();

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
        HideRecordingHud();
        _recordingHudViewModel = hudViewModel;
        _recordingHudViewModel.CloseRequested += OnRecordingHudCloseRequested;
        _recordingHudViewModel.StartElapsedTimer();
        RecordingHudPanel.DataContext = hudViewModel;
        RecordingHudPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PositionRecordingHud));
    }

    private void HideRecordingHud()
    {
        if (_recordingHudViewModel is not null)
        {
            _recordingHudViewModel.CloseRequested -= OnRecordingHudCloseRequested;
            _recordingHudViewModel.CancelElapsedTimer();
            _recordingHudViewModel = null;
        }

        RecordingHudPanel.DataContext = null;
        RecordingHudPanel.Visibility = Visibility.Collapsed;
    }

    private void OnRecordingHudCloseRequested()
    {
        Dispatcher.Invoke(Close);
    }

    private void RecordingHudPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RecordingHudPanel.Visibility == Visibility.Visible)
        {
            PositionRecordingHud();
        }
    }

    private void PositionRecordingHud()
    {
        if (RecordingHudPanel.Visibility != Visibility.Visible
            || RecordingHudPanel.ActualWidth <= 0
            || RecordingHudPanel.ActualHeight <= 0)
        {
            return;
        }

        var (left, top) = OverlayWindow.ComputeRecordingHudPosition(
            _geometry.CaptureRectDips,
            RecordingHudPanel.ActualWidth,
            RecordingHudPanel.ActualHeight,
            _geometry.WorkAreaBoundsDips,
            _userSettings.Current.HudGapPixels);
        Canvas.SetLeft(RecordingHudPanel, left);
        Canvas.SetTop(RecordingHudPanel, top);

        if (!_initialHudDiagnosticsLogged)
        {
            var hudBounds = new Rect(left, top, RecordingHudPanel.ActualWidth, RecordingHudPanel.ActualHeight);
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
            && _recordingHudViewModel?.SelectToolCommand.CanExecute(tag) == true)
        {
            _recordingHudViewModel.SelectToolCommand.Execute(tag);
            RecordingAnnotationCanvas.Cursor = GetRecordingAnnotationCursor();
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
