using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;

namespace SnippingTool;

public partial class OverlayWindow : Window
{
    private const int ImageViewportMargin = 140;
    private const double RecordingBorderStrokeThickness = 2d;
    private const double RecordingBorderClearance = 6d;
    private const double RecordingBorderOffset = RecordingBorderStrokeThickness + RecordingBorderClearance;
    private readonly OverlayViewModel _vm;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IScreenRecordingService _recorder;
    private readonly Func<IScreenRecordingService, string, RecordingHudViewModel> _recordingHudViewModelFactory;
    private readonly IEventAggregator _eventAggregator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUserSettingsService _userSettings;
    private readonly IMessageBoxService _messageBox;
    private readonly IFileSystemService _fileSystem;
    private readonly IOcrService _ocrService;
    private readonly Func<Rect, double, double, RecordingAnnotationWindow> _recordingAnnotationWindowFactory;
    private readonly IEventSubscription _redoSubscription;
    private readonly IEventSubscription _undoSubscription;
    private AnnotationCanvasRenderer _renderer = null!;
    private AnnotationCanvasInteractionController _annotationInteractionController = null!;
    private RecordingBorderWindow? _recordingBorder;
    private RecordingHudWindow? _recordingHud;
    private RecordingAnnotationWindow? _recordingAnnotation;
    private Point? _lassoStart;
    private BitmapSource? _openedImage;
    private string? _openedImagePath;
    private Rect _openedImageDisplayRect;
    private double _openedImageScaleX = 1.0;
    private double _openedImageScaleY = 1.0;
    private BitmapSource? _screenSnapshot;
    private BitmapSource? _pendingPinnedBitmap;

    public OverlayWindow(
        OverlayViewModel vm,
        IScreenCaptureService screenCapture,
        IScreenRecordingService recorder,
        Func<IScreenRecordingService, string, RecordingHudViewModel> recordingHudViewModelFactory,
        IEventAggregator eventAggregator,
        ILoggerFactory loggerFactory,
        IUserSettingsService userSettings,
        IMessageBoxService messageBox,
        IFileSystemService fileSystem,
        IOcrService ocrService,
        Func<Rect, double, double, RecordingAnnotationWindow> recordingAnnotationWindowFactory)
    {
        _vm = vm;
        _screenCapture = screenCapture;
        _recorder = recorder;
        _recordingHudViewModelFactory = recordingHudViewModelFactory;
        _eventAggregator = eventAggregator;
        _loggerFactory = loggerFactory;
        _userSettings = userSettings;
        _messageBox = messageBox;
        _fileSystem = fileSystem;
        _ocrService = ocrService;
        _recordingAnnotationWindowFactory = recordingAnnotationWindowFactory;
        InitializeComponent();
        DataContext = _vm;
        _vm.SetBitmapCapture(new OverlayBitmapCapture(
            this,
            AnnotationCanvas,
            _screenCapture,
            () => _vm.SelectionRect,
            () => _vm.DpiX,
            () => _vm.DpiY));
        _renderer = new AnnotationCanvasRenderer(AnnotationCanvas, _vm, el => _vm.TrackElement(el), loggerFactory.CreateLogger<AnnotationCanvasRenderer>());
        _annotationInteractionController = new AnnotationCanvasInteractionController(AnnotationCanvas, _vm, _renderer);
        _undoSubscription = _eventAggregator.Subscribe<UndoGroupMessage>(HandleUndoGroupAsync);
        _redoSubscription = _eventAggregator.Subscribe<RedoGroupMessage>(HandleRedoGroupAsync);
        _vm.CloseRequested += Close;
        _vm.PinRequested += DoPin;

        Root.MouseLeftButtonDown += Root_MouseDown;
        Root.MouseMove += Root_MouseMove;
        Root.MouseLeftButtonUp += Root_MouseUp;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        DimFull.Width = Width;
        DimFull.Height = Height;
        ScreenSnapshot.Width = Width;
        ScreenSnapshot.Height = Height;

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is not null)
        {
            _vm.DpiX = src.CompositionTarget.TransformToDevice.M11;
            _vm.DpiY = src.CompositionTarget.TransformToDevice.M22;
        }

        if (_openedImage is not null)
        {
            InitializeFromOpenedImage(_openedImage);
            return;
        }

        Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(50);
        _screenSnapshot = _screenCapture.Capture(
            (int)(Left * _vm.DpiX), (int)(Top * _vm.DpiY),
            (int)(Width * _vm.DpiX), (int)(Height * _vm.DpiY));
        ScreenSnapshot.Source = _screenSnapshot;
        Visibility = Visibility.Visible;
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting)
        {
            return;
        }

        var start = e.GetPosition(Root);
        Root.Tag = start; // store drag origin on the element
        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, start.X);
        Canvas.SetTop(SelectionBorder, start.Y);
        SelectionBorder.Width = 0;
        SelectionBorder.Height = 0;
        Root.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting
            || Root.Tag is not Point start)
        {
            return;
        }

        var cur = e.GetPosition(Root);
        var x = Math.Min(cur.X, start.X);
        var y = Math.Min(cur.Y, start.Y);
        var w = Math.Abs(cur.X - start.X);
        var h = Math.Abs(cur.Y - start.Y);

        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = w;
        SelectionBorder.Height = h;

        _vm.UpdateSizeLabel(w, h);
        SizeLabelText.Text = _vm.SizeLabel;
        SizeLabelBorder.Visibility = Visibility.Visible;
        SizeLabelBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var ly = y - SizeLabelBorder.DesiredSize.Height - 4;
        if (ly < 0)
        {
            ly = y + 4;
        }

        Canvas.SetLeft(SizeLabelBorder, x);
        Canvas.SetTop(SizeLabelBorder, ly);

        UpdateLoupe(cur);
    }

    private void Root_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting
            || Root.Tag is not Point start)
        {
            return;
        }

        LoupeBorder.Visibility = Visibility.Collapsed;
        Root.Tag = null;
        Root.ReleaseMouseCapture();

        var end = e.GetPosition(Root);
        var x = Math.Min(end.X, start.X);
        var y = Math.Min(end.Y, start.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);

        if (w < 4 || h < 4)
        {
            Close();
            return;
        }

        _vm.CommitSelection(new Rect(x, y, w, h));
        TransitionToAnnotating();
    }

    private const int LoupeSize = 120;
    private const int LoupeZoom = 4;
    private const int LoupeOffset = 20;

    private void UpdateLoupe(Point cursor)
    {
        if (_screenSnapshot is null)
        {
            return;
        }

        var srcSize = LoupeSize / LoupeZoom;
        var px = (int)(cursor.X * _vm.DpiX) - srcSize / 2;
        var py = (int)(cursor.Y * _vm.DpiY) - srcSize / 2;
        var snapW = _screenSnapshot.PixelWidth;
        var snapH = _screenSnapshot.PixelHeight;
        px = Math.Clamp(px, 0, Math.Max(0, snapW - srcSize));
        py = Math.Clamp(py, 0, Math.Max(0, snapH - srcSize));
        var actualW = Math.Min(srcSize, snapW - px);
        var actualH = Math.Min(srcSize, snapH - py);

        if (actualW <= 0 || actualH <= 0)
        {
            LoupeBorder.Visibility = Visibility.Collapsed;
            return;
        }

        LoupeImage.Source = new CroppedBitmap(_screenSnapshot, new Int32Rect(px, py, actualW, actualH));
        LoupeBorder.Visibility = Visibility.Visible;

        var lx = cursor.X + LoupeOffset;
        var ly = cursor.Y + LoupeOffset;
        if (lx + LoupeSize > Width)
        {
            lx = cursor.X - LoupeSize - LoupeOffset;
        }
        if (ly + LoupeSize > Height)
        {
            ly = cursor.Y - LoupeSize - LoupeOffset;
        }

        Canvas.SetLeft(LoupeBorder, lx);
        Canvas.SetTop(LoupeBorder, ly);
    }

    private void TransitionToAnnotating()
    {
        var sel = _vm.SelectionRect;
        var screenX = (int)((Left + sel.X) * _vm.DpiX);
        var screenY = (int)((Top + sel.Y) * _vm.DpiY);
        var screenW = Math.Max(1, (int)(sel.Width * _vm.DpiX));
        var screenH = Math.Max(1, (int)(sel.Height * _vm.DpiY));
        Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(60);
        var backgroundCapture = _screenCapture.Capture(screenX, screenY, screenW, screenH);
        Visibility = Visibility.Visible;
        _screenSnapshot = null;

        EnterAnnotatingSession(sel, backgroundCapture, _vm.DpiX, _vm.DpiY, allowRecording: true);
    }

    private void InitializeFromOpenedImage(BitmapSource openedImage)
    {
        _openedImageDisplayRect = CalculateOpenedImageDisplayRect(openedImage);
        _openedImageScaleX = openedImage.PixelWidth / _openedImageDisplayRect.Width;
        _openedImageScaleY = openedImage.PixelHeight / _openedImageDisplayRect.Height;

        _vm.SetBitmapCapture(new OpenedImageBitmapCapture(openedImage, AnnotationCanvas));

        ScreenSnapshot.Source = openedImage;
        ScreenSnapshot.Width = _openedImageDisplayRect.Width;
        ScreenSnapshot.Height = _openedImageDisplayRect.Height;
        Canvas.SetLeft(ScreenSnapshot, _openedImageDisplayRect.X);
        Canvas.SetTop(ScreenSnapshot, _openedImageDisplayRect.Y);

        EnterAnnotatingSession(
            _openedImageDisplayRect,
            openedImage,
            _openedImageScaleX,
            _openedImageScaleY,
            allowRecording: false);
    }

    private Rect CalculateOpenedImageDisplayRect(BitmapSource openedImage)
    {
        return CalculateOpenedImageDisplayRect(
            openedImage.PixelWidth,
            openedImage.PixelHeight,
            GetOpenedImageTargetArea(),
            ImageViewportMargin);
    }

    internal static Rect CalculateOpenedImageDisplayRect(
        double imagePixelWidth,
        double imagePixelHeight,
        Rect targetArea,
        double viewportMargin)
    {
        var maxWidth = Math.Max(1d, targetArea.Width - (viewportMargin * 2d));
        var maxHeight = Math.Max(1d, targetArea.Height - (viewportMargin * 2d));
        var scale = Math.Min(maxWidth / imagePixelWidth, maxHeight / imagePixelHeight);
        scale = Math.Min(1d, scale);

        var displayWidth = Math.Max(1d, imagePixelWidth * scale);
        var displayHeight = Math.Max(1d, imagePixelHeight * scale);
        var left = targetArea.Left + ((targetArea.Width - displayWidth) / 2d);
        var top = targetArea.Top + ((targetArea.Height - displayHeight) / 2d);

        return new Rect(left, top, displayWidth, displayHeight);
    }

    private Rect GetOpenedImageTargetArea()
    {
        var targetScreen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var workingArea = targetScreen.WorkingArea;

        return new Rect(
            (workingArea.Left / _vm.DpiX) - Left,
            (workingArea.Top / _vm.DpiY) - Top,
            workingArea.Width / _vm.DpiX,
            workingArea.Height / _vm.DpiY);
    }

    private void EnterAnnotatingSession(Rect selectionRect, BitmapSource backgroundBitmap, double pixelScaleX, double pixelScaleY, bool allowRecording)
    {
        _vm.InitializeAnnotatingSession(selectionRect, pixelScaleX, pixelScaleY);

        Cursor = Cursors.Arrow;
        SizeLabelBorder.Visibility = Visibility.Collapsed;
        LoupeBorder.Visibility = Visibility.Collapsed;
        DimFull.Visibility = Visibility.Collapsed;

        Canvas.SetLeft(SelectionBorder, selectionRect.X);
        Canvas.SetTop(SelectionBorder, selectionRect.Y);
        SelectionBorder.Width = selectionRect.Width;
        SelectionBorder.Height = selectionRect.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        _renderer.SetBackground(backgroundBitmap, pixelScaleX, pixelScaleY);

        LayoutDimStrips(selectionRect);

        AnnotationCanvas.Width = selectionRect.Width;
        AnnotationCanvas.Height = selectionRect.Height;
        Canvas.SetLeft(AnnotationCanvas, selectionRect.X);
        Canvas.SetTop(AnnotationCanvas, selectionRect.Y);
        AnnotationCanvas.Visibility = Visibility.Visible;
        AnnotationCanvas.Cursor = Cursors.Cross;

        AnnotationCanvas.MouseLeftButtonDown += Annot_Down;
        AnnotationCanvas.MouseMove += Annot_Move;
        AnnotationCanvas.MouseLeftButtonUp += Annot_Up;

        RecordBtn.Visibility = allowRecording ? Visibility.Visible : Visibility.Collapsed;
        CompactRecordBtn.Visibility = allowRecording ? Visibility.Visible : Visibility.Collapsed;

        PositionToolbars(selectionRect);
    }

    private void LayoutDimStrips(Rect s)
    {
        var sw = Width;
        var sh = Height;

        DimTop.SetValue(Canvas.LeftProperty, 0d);
        DimTop.SetValue(Canvas.TopProperty, 0d);
        DimTop.Width = sw;
        DimTop.Height = s.Top;
        DimTop.Visibility = Visibility.Visible;

        DimBottom.SetValue(Canvas.LeftProperty, 0d);
        DimBottom.SetValue(Canvas.TopProperty, s.Bottom);
        DimBottom.Width = sw;
        DimBottom.Height = sh - s.Bottom;
        DimBottom.Visibility = Visibility.Visible;

        DimLeft.SetValue(Canvas.LeftProperty, 0d);
        DimLeft.SetValue(Canvas.TopProperty, s.Top);
        DimLeft.Width = s.Left;
        DimLeft.Height = s.Height;
        DimLeft.Visibility = Visibility.Visible;

        DimRight.SetValue(Canvas.LeftProperty, s.Right);
        DimRight.SetValue(Canvas.TopProperty, s.Top);
        DimRight.Width = sw - s.Right;
        DimRight.Height = s.Height;
        DimRight.Visibility = Visibility.Visible;
    }

    private void PositionToolbars(Rect sel)
    {
        var toolSize = MeasureFloatingElement(AnnotToolbar);
        var fullActionSize = MeasureFloatingElement(ActionBar);
        var compactActionSize = MeasureFloatingElement(CompactActionBar);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            sel,
            new Size(Width, Height),
            toolSize,
            fullActionSize,
            compactActionSize);

        AnnotToolbar.Visibility = Visibility.Visible;
        Canvas.SetLeft(AnnotToolbar, layout.ToolBounds.Left);
        Canvas.SetTop(AnnotToolbar, layout.ToolBounds.Top);

        var useFullActionBar = layout.ActionBarMode == OverlayActionBarMode.Full;
        ActionBar.Visibility = useFullActionBar ? Visibility.Visible : Visibility.Collapsed;
        CompactActionBar.Visibility = useFullActionBar ? Visibility.Collapsed : Visibility.Visible;

        var actionBar = useFullActionBar ? ActionBar : CompactActionBar;
        Canvas.SetLeft(actionBar, layout.ActionBounds.Left);
        Canvas.SetTop(actionBar, layout.ActionBounds.Top);
    }

    private static Size MeasureFloatingElement(FrameworkElement element)
    {
        var originalVisibility = element.Visibility;
        if (originalVisibility == Visibility.Collapsed)
        {
            element.Visibility = Visibility.Hidden;
        }

        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredSize = element.DesiredSize;
        element.Visibility = originalVisibility;
        return desiredSize;
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
                _ = DoLassoOcrAsync(new Rect(x, y, w, h));
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

    private void Record_Click(object sender, RoutedEventArgs e) => StartRecordingSession();

    private void StartRecordingSession()
    {
        var sel = _vm.SelectionRect;
        var screenX = (int)((Left + sel.X) * _vm.DpiX);
        var screenY = (int)((Top + sel.Y) * _vm.DpiY);
        var screenW = (int)(sel.Width * _vm.DpiX);
        var screenH = (int)(sel.Height * _vm.DpiY);

        var videosDir = _userSettings.Current.RecordingOutputPath;
        _fileSystem.CreateDirectory(videosDir);
        var ext = _userSettings.Current.RecordingFormat == Models.RecordingFormat.Mp4 ? ".mp4" : ".avi";
        var path = _fileSystem.CombinePath(videosDir, $"SnipRec-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");

        try
        {
            _recorder.Start(screenX, screenY, screenW, screenH, path);
        }
        catch (System.IO.FileNotFoundException ex)
        {
            _messageBox.ShowWarning(ex.Message, "ffmpeg not found");
            return;
        }

        Visibility = Visibility.Hidden;

        var regionRect = new Rect(Left + sel.X, Top + sel.Y, sel.Width, sel.Height);
        ShowRecordingSessionWindows(regionRect, path);
    }

    private void ShowRecordingSessionWindows(Rect regionRect, string outputPath)
    {
        var borderRect = new Rect(
            regionRect.Left - RecordingBorderOffset,
            regionRect.Top - RecordingBorderOffset,
            regionRect.Width + (RecordingBorderOffset * 2d),
            regionRect.Height + (RecordingBorderOffset * 2d));

        _recordingBorder = new RecordingBorderWindow(borderRect.Left, borderRect.Top, borderRect.Width, borderRect.Height);
        _recordingBorder.Show();

        _recordingAnnotation = _recordingAnnotationWindowFactory(regionRect, _vm.DpiX, _vm.DpiY);
        _recordingAnnotation.Show();

        var hudVm = _recordingHudViewModelFactory(_recorder, outputPath);
        hudVm.AttachAnnotationSession(_recordingAnnotation.ViewModel, () => _recordingAnnotation?.ToggleInputMode() ?? false);
        hudVm.StopCompleted += HandleRecordingStopCompleted;
        _recordingHud = new RecordingHudWindow(hudVm, regionRect, _userSettings);
        _recordingHud.Show();
    }

    private void HandleRecordingStopCompleted()
    {
        Dispatcher.Invoke(() =>
        {
            CloseRecordingSessionWindows();
            Close();
        });
    }

    private void CloseRecordingSessionWindows()
    {
        _recordingHud?.Close();
        _recordingHud = null;

        _recordingAnnotation?.Close();
        _recordingAnnotation = null;

        _recordingBorder?.Close();
        _recordingBorder = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        var pendingPinnedBitmap = _pendingPinnedBitmap;
        _pendingPinnedBitmap = null;

        _undoSubscription.Dispose();
        _redoSubscription.Dispose();

        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }

        CloseRecordingSessionWindows();

        base.OnClosed(e);

        if (pendingPinnedBitmap is not null)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    var pinned = new PinnedScreenshotWindow(pendingPinnedBitmap);
                    pinned.Show();
                }));
        }
    }

    private ValueTask HandleUndoGroupAsync(UndoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            AnnotationCanvas.Children.Remove(element);
        }

        ResetNumberCounter();
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRedoGroupAsync(RedoGroupMessage message)
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

    private async Task DoLassoOcrAsync(Rect lassoRect)
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
        var text = await _ocrService.RecognizeAsync(cropped);

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
