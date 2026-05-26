using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;

namespace Pointframe;

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
    private readonly ITelemetryService _telemetry;
    private readonly RecordingAnnotationViewModel _recordingAnnotationViewModel;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventSubscription _redoSubscription;
    private readonly IEventSubscription _undoSubscription;
    private readonly Func<BitmapSource, BeautifierWindow> _beautifierWindowFactory;
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
    private BeautifierWindow? _pendingBeautifierWindow;
    private bool _closeLeavesRecorderRunning;
    private SelectionSessionMode _selectionSessionMode = SelectionSessionMode.Region;
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
        ITelemetryService telemetry,
        RecordingAnnotationViewModel recordingAnnotationViewModel,
        Func<BitmapSource, BeautifierWindow> beautifierWindowFactory)
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
        _telemetry = telemetry;
        _recordingAnnotationViewModel = recordingAnnotationViewModel;
        _beautifierWindowFactory = beautifierWindowFactory;
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
        _annotationInteractionController = new AnnotationCanvasInteractionController(
            AnnotationCanvas, _vm, _renderer,
            onColorPicked: (color, pt) =>
            {
                SyncToolbarToSelectedTool();
                AnnotationCanvas.Cursor = _vm.SelectedTool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
                var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                System.Windows.Clipboard.SetText(hex);
                ShowOcrToast($"Copied {hex}");
            },
            onLoupePositionChanged: pt => UpdateLoupe(pt));
        _undoSubscription = _eventAggregator.Subscribe<UndoGroupMessage>(HandleUndoGroup);
        _redoSubscription = _eventAggregator.Subscribe<RedoGroupMessage>(HandleRedoGroup);
        _vm.CloseRequested += Close;
        _vm.PinRequested += DoPin;
        _vm.BeautifyRequested += DoBeautify;

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
        // Set window bounds before Show() so WPF creates the HWND directly on the target monitor.
        // If bounds are deferred to OnSourceInitialized, the HWND is first created on the primary
        // monitor, WPF caps Width to that monitor's physical width, and the value reads back wrong
        // (e.g. 2560 DIPs → 1724 on a 148.5% DPI primary monitor instead of the correct 2560).
        // SelectionMonitorWindow uses the same pattern: it sets bounds in its constructor,
        // which also runs before Show().
        Left = selectionSession.HostBoundsDips.Left;
        Top = selectionSession.HostBoundsDips.Top;
        Width = selectionSession.HostBoundsDips.Width;
        Height = selectionSession.HostBoundsDips.Height;
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

        _selectionSessionMode = selectionSession.SessionMode;
        _vm.CommitSelection(selectionSession.SelectionRectDips, selectionSession.SelectionBoundsPixels);
        EnterAnnotatingSession(
            selectionSession.SelectionRectDips,
            selectionSession.SelectionBackground,
            selectionSession.DpiScaleX,
            selectionSession.DpiScaleY,
            allowRecording: true,
            selectionSession.SessionMode);
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
        var pendingBeautifierWindow = _pendingBeautifierWindow;
        _pendingBeautifierWindow = null;
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

        if (pendingBeautifierWindow is not null)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    if (System.Windows.Application.Current is App app)
                    {
                        app.RegisterAutomationWindow(pendingBeautifierWindow);
                    }

                    pendingBeautifierWindow.Show();
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

    private void DoBeautify(BitmapSource bitmap)
    {
        _pendingBeautifierWindow = _beautifierWindowFactory(bitmap);
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
        _telemetry.TrackEvent("ocr_used");
        ShowOcrToast("\u2713 Text copied to clipboard");
    }

    private async void ShowOcrToast(string message)
    {
        OcrToastText.Text = message;
        OcrToast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toastSize = OcrToast.DesiredSize;

        if (_selectionSessionMode == SelectionSessionMode.FullScreen)
        {
            PositionOcrToastNearActionBar(toastSize);
        }
        else
        {
            var selection = _vm.SelectionRect;
            Canvas.SetLeft(OcrToast, selection.X + ((selection.Width - toastSize.Width) / 2d));
            Canvas.SetTop(OcrToast, selection.Y + ((selection.Height - toastSize.Height) / 2d));
        }

        OcrToast.Visibility = Visibility.Visible;

        await Task.Delay(1500);
        OcrToast.Visibility = Visibility.Collapsed;
    }

    private void PositionOcrToastNearActionBar(Size toastSize)
    {
        FrameworkElement? actionBar = null;
        if (ActionBar.Visibility == Visibility.Visible)
        {
            actionBar = ActionBar;
        }
        else if (CompactActionBar.Visibility == Visibility.Visible)
        {
            actionBar = CompactActionBar;
        }

        if (actionBar is null)
        {
            var selection = _vm.SelectionRect;
            Canvas.SetLeft(OcrToast, selection.X + ((selection.Width - toastSize.Width) / 2d));
            Canvas.SetTop(OcrToast, selection.Y + ((selection.Height - toastSize.Height) / 2d));
            return;
        }

        actionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var actionBarSize = actionBar.RenderSize.Width > 0d && actionBar.RenderSize.Height > 0d
            ? actionBar.RenderSize
            : actionBar.DesiredSize;
        var left = Canvas.GetLeft(actionBar) + ((actionBarSize.Width - toastSize.Width) / 2d);
        var top = Canvas.GetTop(actionBar) + actionBarSize.Height + 10d;

        if (top + toastSize.Height > Height - 16d)
        {
            top = Canvas.GetTop(actionBar) - toastSize.Height - 10d;
        }

        Canvas.SetLeft(OcrToast, Math.Max(16d, Math.Min(left, Width - toastSize.Width - 16d)));
        Canvas.SetTop(OcrToast, Math.Max(16d, Math.Min(top, Height - toastSize.Height - 16d)));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleOverlayShortcut(e.Key, e.KeyboardDevice.Modifiers))
        {
            e.Handled = true;
            return;
        }
    }

    private bool HandleOverlayShortcut(Key key, ModifierKeys modifiers)
    {
        var shortcuts = _userSettings.Current;
        if (MatchesShortcut(key, modifiers, shortcuts.OverlayCloseHotkey, shortcuts.OverlayCloseHotkeyModifiers))
        {
            if (_vm.CurrentPhase == OverlayViewModel.Phase.Annotating)
            {
                if (ShortcutsPopup.Visibility == Visibility.Visible)
                {
                    ShortcutsPopup.Visibility = Visibility.Collapsed;
                    return true;
                }

                if (_vm.IsTextLassoActive)
                {
                    _vm.IsTextLassoActive = false;
                    OcrLassoRect.Visibility = Visibility.Collapsed;
                    _lassoStart = null;
                    return true;
                }

                if (_vm.SelectedTool == AnnotationTool.ColorPicker)
                {
                    _vm.RevertToPreviousTool();
                    SyncToolbarToSelectedTool();
                    UpdateLoupe(null);
                    AnnotationCanvas.Cursor = _vm.SelectedTool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
                    return true;
                }
            }

            Close();
            return true;
        }

        if (_vm.CurrentPhase != OverlayViewModel.Phase.Annotating)
        {
            return false;
        }

        if (key == Key.Escape)
        {
            if (ShortcutsPopup.Visibility == Visibility.Visible)
            {
                ShortcutsPopup.Visibility = Visibility.Collapsed;
                return true;
            }

            if (_vm.IsTextLassoActive)
            {
                _vm.IsTextLassoActive = false;
                OcrLassoRect.Visibility = Visibility.Collapsed;
                _lassoStart = null;
                return true;
            }

            if (_vm.SelectedTool == AnnotationTool.ColorPicker)
            {
                _vm.RevertToPreviousTool();
                SyncToolbarToSelectedTool();
                UpdateLoupe(null);
                AnnotationCanvas.Cursor = _vm.SelectedTool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
                return true;
            }
        }

        if (MatchesShortcut(key, modifiers, shortcuts.OverlayToggleShortcutsHotkey, shortcuts.OverlayToggleShortcutsHotkeyModifiers))
        {
            ToggleShortcutsPopup();
            return true;
        }

        if (MatchesShortcut(key, modifiers, shortcuts.OverlayCopyHotkey, shortcuts.OverlayCopyHotkeyModifiers))
        {
            if (_vm.CopyCommand.CanExecute(null))
            {
                _vm.CopyCommand.Execute(null);
            }

            return true;
        }

        if (MatchesShortcut(key, modifiers, shortcuts.OverlaySaveAsHotkey, shortcuts.OverlaySaveAsHotkeyModifiers))
        {
            if (_vm.SaveAsCommand.CanExecute(null))
            {
                _vm.SaveAsCommand.Execute(null);
            }

            return true;
        }

        if (MatchesShortcut(key, modifiers, shortcuts.OverlayUndoHotkey, shortcuts.OverlayUndoHotkeyModifiers))
        {
            if (_vm.UndoCommand.CanExecute(null))
            {
                _vm.UndoCommand.Execute(null);
            }

            return true;
        }

        if (MatchesShortcut(key, modifiers, shortcuts.OverlayRedoHotkey, shortcuts.OverlayRedoHotkeyModifiers))
        {
            if (_vm.RedoCommand.CanExecute(null))
            {
                _vm.RedoCommand.Execute(null);
            }

            return true;
        }

#if DEBUG
        if (key == Key.F12 && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            throw new InvalidOperationException("Debug-only UI recovery smoke test.");
        }
#endif

        return false;
    }

    private static bool MatchesShortcut(Key key, ModifierKeys pressedModifiers, uint configuredKey, HotkeyModifiers configuredModifiers)
    {
        if (configuredKey == 0)
        {
            return false;
        }

        return key == KeyInterop.KeyFromVirtualKey((int)configuredKey)
               && pressedModifiers == ToModifierKeys(configuredModifiers);
    }

    private static ModifierKeys ToModifierKeys(HotkeyModifiers modifiers)
    {
        var result = ModifierKeys.None;
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            result |= ModifierKeys.Control;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= ModifierKeys.Shift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= ModifierKeys.Alt;
        }

        return result;
    }

    private void ToggleShortcutsPopup()
    {
        if (ShortcutsPopup.Visibility == Visibility.Visible)
        {
            ShortcutsPopup.Visibility = Visibility.Collapsed;
            return;
        }

        ShortcutsPopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var popupSize = ShortcutsPopup.DesiredSize;
        var popupWidth = Math.Max(ShortcutsPopup.MinWidth, popupSize.Width);
        var popupHeight = popupSize.Height;

        var preferredLeft = Width - popupWidth - 16d;
        var preferredTop = 24d;

        if (AnnotToolbar.Visibility == Visibility.Visible)
        {
            AnnotToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var toolbarSize = AnnotToolbar.RenderSize.Width > 0d && AnnotToolbar.RenderSize.Height > 0d
                ? AnnotToolbar.RenderSize
                : AnnotToolbar.DesiredSize;
            var toolbarLeft = Canvas.GetLeft(AnnotToolbar);
            var toolbarTop = Canvas.GetTop(AnnotToolbar);

            preferredLeft = toolbarLeft - popupWidth - 12d;
            preferredTop = toolbarTop + ((toolbarSize.Height - popupHeight) / 2d);
        }

        var left = Math.Max(16d, Math.Min(preferredLeft, Width - popupWidth - 16d));
        var top = Math.Max(16d, Math.Min(preferredTop, Height - popupHeight - 16d));
        Canvas.SetLeft(ShortcutsPopup, left);
        Canvas.SetTop(ShortcutsPopup, top);
        ShortcutsPopup.Visibility = Visibility.Visible;
    }
}
