using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;

namespace SnippingTool;

public partial class OverlayWindow : Window
{
    private const int ImageViewportMargin = 140;
    private readonly OverlayViewModel _vm;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IScreenRecordingService _recorder;
    private readonly IMouseHookService _mouseHookService;
    private readonly Func<IScreenRecordingService, string, RecordingHudViewModel> _recordingHudViewModelFactory;
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<OverlayWindow> _logger;
    private readonly IUserSettingsService _userSettings;
    private readonly IMessageBoxService _messageBox;
    private readonly IFileSystemService _fileSystem;
    private readonly IOcrService _ocrService;
    private readonly RecordingAnnotationViewModel _recordingAnnotationViewModel;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventSubscription _redoSubscription;
    private readonly IEventSubscription _undoSubscription;
    private AnnotationCanvasRenderer _renderer = null!;
    private AnnotationCanvasInteractionController _annotationInteractionController = null!;
    private Point? _lassoStart;
    private RecordingSessionGeometry _recordingSessionGeometry = RecordingSessionGeometry.Empty;
    private BitmapSource? _openedImage;
    private string? _openedImagePath;
    private Rect _openedImageDisplayRect;
    private double _openedImageScaleX = 1.0;
    private double _openedImageScaleY = 1.0;
    private string? _annotatingMonitorName;
    private BitmapSource? _annotatingMonitorSnapshot;
    private BitmapSource? _pendingPinnedBitmap;
    private bool _closeLeavesRecorderRunning;
    private SelectionSessionResult? _pendingSelectionSession;
    private readonly List<SelectionBackdropWindow> _annotatingBackdropWindows = [];

    internal OverlayWindow(
        OverlayViewModel vm,
        IScreenCaptureService screenCapture,
        IScreenRecordingService recorder,
        IMouseHookService mouseHookService,
        Func<IScreenRecordingService, string, RecordingHudViewModel> recordingHudViewModelFactory,
        IEventAggregator eventAggregator,
        ILoggerFactory loggerFactory,
        IUserSettingsService userSettings,
        IMessageBoxService messageBox,
        IFileSystemService fileSystem,
        IOcrService ocrService,
        RecordingAnnotationViewModel recordingAnnotationViewModel)
    {
        _vm = vm;
        _screenCapture = screenCapture;
        _recorder = recorder;
        _mouseHookService = mouseHookService;
        _recordingHudViewModelFactory = recordingHudViewModelFactory;
        _eventAggregator = eventAggregator;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OverlayWindow>();
        _userSettings = userSettings;
        _messageBox = messageBox;
        _fileSystem = fileSystem;
        _ocrService = ocrService;
        _recordingAnnotationViewModel = recordingAnnotationViewModel;
        InitializeComponent();
        DataContext = _vm;
        _vm.SetBitmapCapture(new OverlayBitmapCapture(
            this,
            AnnotationCanvas,
            _screenCapture,
            () => _vm.SelectionRect,
            () => _vm.SelectionScreenBoundsPixels,
            () => _vm.DpiX,
            () => _vm.DpiY));
        _renderer = new AnnotationCanvasRenderer(AnnotationCanvas, _vm, el => _vm.TrackElement(el), loggerFactory.CreateLogger<AnnotationCanvasRenderer>());
        _annotationInteractionController = new AnnotationCanvasInteractionController(AnnotationCanvas, _vm, _renderer);
        _undoSubscription = _eventAggregator.Subscribe<UndoGroupMessage>(HandleUndoGroup);
        _redoSubscription = _eventAggregator.Subscribe<RedoGroupMessage>(HandleRedoGroup);
        _vm.CloseRequested += Close;
        _vm.PinRequested += DoPin;

        KeyDown += Window_KeyDown;

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayViewModel.IsTextLassoActive))
            {
                if (_vm.CurrentPhase == OverlayViewModel.Phase.Annotating)
                {
                    AnnotationCanvas.Cursor = _vm.IsTextLassoActive
                        ? Cursors.Cross
                        : _vm.SelectedTool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
                }
            }
        };
    }

    public void InitializeFromImage(BitmapSource bitmap, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        _openedImage = bitmap;
        _openedImagePath = sourcePath;
    }

    internal void InitializeFromSelectionSession(SelectionSessionResult selectionSession)
    {
        ArgumentNullException.ThrowIfNull(selectionSession);
        _pendingSelectionSession = selectionSession;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (_pendingSelectionSession is not null)
        {
            InitializeFromSelectionSessionCore(_pendingSelectionSession);
            _pendingSelectionSession = null;
            return;
        }

        if (_openedImage is not null)
        {
            var targetScreen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var monitorScale = MonitorDpiHelper.GetMonitorScale(targetScreen.Bounds.Location);
            var hostBoundsDips = MonitorDpiHelper.CalculateWindowBounds(targetScreen.Bounds, monitorScale);
            Left = hostBoundsDips.Left;
            Top = hostBoundsDips.Top;
            Width = hostBoundsDips.Width;
            Height = hostBoundsDips.Height;
            ScreenSnapshot.Width = Width;
            ScreenSnapshot.Height = Height;
            _vm.DpiX = monitorScale;
            _vm.DpiY = monitorScale;
            InitializeFromOpenedImage(_openedImage);
            return;
        }

        throw new InvalidOperationException("OverlayWindow must be initialized from a selection session or an opened image.");
    }

    private void InitializeFromSelectionSessionCore(SelectionSessionResult selectionSession)
    {
        Left = selectionSession.HostBoundsDips.Left;
        Top = selectionSession.HostBoundsDips.Top;
        Width = selectionSession.HostBoundsDips.Width;
        Height = selectionSession.HostBoundsDips.Height;

        _annotatingMonitorName = selectionSession.MonitorName;
        _annotatingMonitorSnapshot = selectionSession.MonitorSnapshot;
        ScreenSnapshot.Source = selectionSession.SelectionBackground;
        ScreenSnapshot.Width = selectionSession.SelectionRectDips.Width;
        ScreenSnapshot.Height = selectionSession.SelectionRectDips.Height;
        Canvas.SetLeft(ScreenSnapshot, selectionSession.SelectionRectDips.X);
        Canvas.SetTop(ScreenSnapshot, selectionSession.SelectionRectDips.Y);

        _vm.DpiX = selectionSession.DpiScaleX;
        _vm.DpiY = selectionSession.DpiScaleY;

        _logger.LogDebug(
            "Overlay annotating session initialized: monitor={Monitor} left={Left} top={Top} width={Width} height={Height} selectionPx={SelX},{SelY},{SelW},{SelH}",
            selectionSession.MonitorName,
            Left,
            Top,
            Width,
            Height,
            selectionSession.SelectionBoundsPixels.X,
            selectionSession.SelectionBoundsPixels.Y,
            selectionSession.SelectionBoundsPixels.Width,
            selectionSession.SelectionBoundsPixels.Height);

        _vm.CommitSelection(selectionSession.SelectionRectDips, selectionSession.SelectionBoundsPixels);
        EnterAnnotatingSession(
            selectionSession.SelectionRectDips,
            selectionSession.SelectionBackground,
            selectionSession.DpiScaleX,
            selectionSession.DpiScaleY,
            allowRecording: true);
    }

    private void Annot_Down(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsTextLassoActive)
        {
            _lassoStart = e.GetPosition(AnnotationCanvas);
            var sel = _vm.SelectionRect;
            Canvas.SetLeft(OcrLassoRect, sel.X + _lassoStart.Value.X);
            Canvas.SetTop(OcrLassoRect, sel.Y + _lassoStart.Value.Y);
            OcrLassoRect.Width = 0;
            OcrLassoRect.Height = 0;
            OcrLassoRect.Visibility = Visibility.Visible;
            AnnotationCanvas.CaptureMouse();
            return;
        }

        _annotationInteractionController.HandlePointerDown(e.GetPosition(AnnotationCanvas));
    }

    private void Annot_Move(object sender, MouseEventArgs e)
    {
        if (_vm.IsTextLassoActive && _lassoStart.HasValue)
        {
            var cur = e.GetPosition(AnnotationCanvas);
            var sel = _vm.SelectionRect;
            var x = Math.Min(cur.X, _lassoStart.Value.X);
            var y = Math.Min(cur.Y, _lassoStart.Value.Y);
            var w = Math.Abs(cur.X - _lassoStart.Value.X);
            var h = Math.Abs(cur.Y - _lassoStart.Value.Y);
            Canvas.SetLeft(OcrLassoRect, sel.X + x);
            Canvas.SetTop(OcrLassoRect, sel.Y + y);
            OcrLassoRect.Width = w;
            OcrLassoRect.Height = h;
            return;
        }

        if (!_vm.IsDragging)
        {
            return;
        }

        _annotationInteractionController.HandlePointerMove(e.GetPosition(AnnotationCanvas));
    }

    private void Annot_Up(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsTextLassoActive && _lassoStart.HasValue)
        {
            var cur = e.GetPosition(AnnotationCanvas);
            AnnotationCanvas.ReleaseMouseCapture();
            var x = Math.Min(cur.X, _lassoStart.Value.X);
            var y = Math.Min(cur.Y, _lassoStart.Value.Y);
            var w = Math.Abs(cur.X - _lassoStart.Value.X);
            var h = Math.Abs(cur.Y - _lassoStart.Value.Y);
            OcrLassoRect.Visibility = Visibility.Collapsed;
            _lassoStart = null;

            if (w >= 4 && h >= 4)
            {
                _ = DoLassoOcr(new Rect(x, y, w, h));
            }

            return;
        }

        if (!_vm.IsDragging)
        {
            return;
        }

        _annotationInteractionController.HandlePointerUp(e.GetPosition(AnnotationCanvas));
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag })
        {
            _vm.IsTextLassoActive = false;
            _vm.SelectedTool = Enum.Parse<AnnotationTool>(tag);
            AnnotationCanvas.Cursor = _vm.SelectedTool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowAnnotatingBackdropWindows()
    {
        CloseAnnotatingBackdropWindows();

        foreach (var screen in Forms.Screen.AllScreens)
        {
            var snapshot = screen.DeviceName == _annotatingMonitorName && _annotatingMonitorSnapshot is not null
                ? _annotatingMonitorSnapshot
                : _screenCapture.Capture(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            var monitorScale = MonitorDpiHelper.GetMonitorScale(screen.Bounds.Location);
            var bounds = MonitorDpiHelper.CalculateWindowBounds(screen.Bounds, monitorScale);
            var backdropWindow = new SelectionBackdropWindow(snapshot, bounds);
            _annotatingBackdropWindows.Add(backdropWindow);
            DpiAwarenessScope.RunPerMonitorV2(() => backdropWindow.Show());

            _logger.LogDebug(
                "Annotating backdrop initialized: monitor={Monitor} left={Left} top={Top} width={Width} height={Height}",
                screen.DeviceName,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height);
        }

        var topmost = Topmost;
        Topmost = false;
        Topmost = topmost;
        Activate();
    }

    private void CloseAnnotatingBackdropWindows()
    {
        foreach (var backdropWindow in _annotatingBackdropWindows)
        {
            backdropWindow.Close();
        }

        _annotatingBackdropWindows.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        var pendingPinnedBitmap = _pendingPinnedBitmap;
        _pendingPinnedBitmap = null;
        CloseAnnotatingBackdropWindows();

        _undoSubscription.Dispose();
        _redoSubscription.Dispose();

        if (!_closeLeavesRecorderRunning && _recorder.IsRecording)
        {
            _recorder.Stop();
        }

        if (!_closeLeavesRecorderRunning)
        {
            CloseRecordingSessionWindows();
        }

        base.OnClosed(e);

        if (pendingPinnedBitmap is not null)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    var pinned = new PinnedScreenshotWindow(pendingPinnedBitmap);
                    if (System.Windows.Application.Current is App app)
                    {
                        app.RegisterAutomationWindow(pinned);
                    }

                    pinned.Show();
                }));
        }
    }

    private ValueTask HandleUndoGroup(UndoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            AnnotationCanvas.Children.Remove(element);
        }

        ResetNumberCounter();
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRedoGroup(RedoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            AnnotationCanvas.Children.Add(element);
        }

        ResetNumberCounter();
        return ValueTask.CompletedTask;
    }

    private void ResetNumberCounter()
    {
        _vm.ResetNumberCounter(AnnotationCanvas.Children
            .OfType<FrameworkElement>()
            .Count(fe => fe.Tag is "number"));
    }

    private void DoPin(BitmapSource bitmap)
    {
        _pendingPinnedBitmap = bitmap;
        Visibility = Visibility.Hidden;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Close));
    }

    private async Task DoLassoOcr(Rect lassoRect)
    {
        var background = _renderer.BackgroundCapture;
        if (background is null)
        {
            return;
        }

        var pixelX = (int)(lassoRect.X * _vm.DpiX);
        var pixelY = (int)(lassoRect.Y * _vm.DpiY);
        var pixelW = (int)(lassoRect.Width * _vm.DpiX);
        var pixelH = (int)(lassoRect.Height * _vm.DpiY);

        pixelX = Math.Max(0, Math.Min(pixelX, background.PixelWidth - 1));
        pixelY = Math.Max(0, Math.Min(pixelY, background.PixelHeight - 1));
        pixelW = Math.Min(pixelW, background.PixelWidth - pixelX);
        pixelH = Math.Min(pixelH, background.PixelHeight - pixelY);

        if (pixelW < 1 || pixelH < 1)
        {
            return;
        }

        var cropped = new CroppedBitmap(background, new Int32Rect(pixelX, pixelY, pixelW, pixelH));
        var text = await _ocrService.Recognize(cropped);

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowOcrToast("No text detected \u2014 try a larger area");
            return;
        }

        System.Windows.Clipboard.SetText(text);
        ShowOcrToast("\u2713 Text copied to clipboard");
    }

    private async void ShowOcrToast(string message)
    {
        OcrToastText.Text = message;
        var sel = _vm.SelectionRect;
        OcrToast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = OcrToast.DesiredSize;
        Canvas.SetLeft(OcrToast, sel.X + (sel.Width - sz.Width) / 2);
        Canvas.SetTop(OcrToast, sel.Y + (sel.Height - sz.Height) / 2);
        OcrToast.Visibility = Visibility.Visible;

        await Task.Delay(1500);
        OcrToast.Visibility = Visibility.Collapsed;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_vm.IsTextLassoActive)
                {
                    _vm.IsTextLassoActive = false;
                    OcrLassoRect.Visibility = Visibility.Collapsed;
                    _lassoStart = null;
                }
                else
                {
                    Close();
                }

                break;
#if DEBUG
            case Key.F12 when e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                throw new InvalidOperationException("Debug-only UI recovery smoke test.");
#endif
            case Key.C when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _vm.CurrentPhase == OverlayViewModel.Phase.Annotating:
                if (_vm.CopyCommand.CanExecute(null))
                {
                    _vm.CopyCommand.Execute(null);
                }

                break;
            case Key.Z when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _vm.CurrentPhase == OverlayViewModel.Phase.Annotating:
                if (_vm.UndoCommand.CanExecute(null))
                {
                    _vm.UndoCommand.Execute(null);
                }

                break;
            case Key.Y when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _vm.CurrentPhase == OverlayViewModel.Phase.Annotating:
                if (_vm.RedoCommand.CanExecute(null))
                {
                    _vm.RedoCommand.Execute(null);
                }

                break;
        }
    }
}
