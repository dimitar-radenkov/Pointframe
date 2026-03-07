using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SnippingTool;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IScreenRecordingService _recorder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUserSettingsService _userSettings;
    private readonly IProcessService _processService;
    private readonly IMessageBoxService _messageBox;
    private readonly IFileSystemService _fileSystem;
    private AnnotationCanvasRenderer _renderer = null!;
    private RecordingBorderWindow? _recordingBorder;
    private RecordingHudWindow? _recordingHud;

    public OverlayWindow(
        OverlayViewModel vm,
        IScreenCaptureService screenCapture,
        IScreenRecordingService recorder,
        ILoggerFactory loggerFactory,
        IUserSettingsService userSettings,
        IProcessService processService,
        IMessageBoxService messageBox,
        IFileSystemService fileSystem)
    {
        _vm = vm;
        _screenCapture = screenCapture;
        _recorder = recorder;
        _loggerFactory = loggerFactory;
        _userSettings = userSettings;
        _processService = processService;
        _messageBox = messageBox;
        _fileSystem = fileSystem;
        InitializeComponent();
        DataContext = _vm;
        _renderer = new AnnotationCanvasRenderer(AnnotationCanvas, _vm, el => _vm.TrackElement(el), loggerFactory.CreateLogger<AnnotationCanvasRenderer>());

        _vm.UndoApplied += group =>
        {
            foreach (var el in group.Cast<UIElement>())
            {
                AnnotationCanvas.Children.Remove(el);
            }

            _vm.ResetNumberCounter(AnnotationCanvas.Children
                .OfType<TextBlock>()
                .Count(tb => tb.Tag is "number"));
        };
        _vm.RedoApplied += group =>
        {
            foreach (var el in group.Cast<UIElement>())
            {
                AnnotationCanvas.Children.Add(el);
            }

            _vm.ResetNumberCounter(AnnotationCanvas.Children
                .OfType<TextBlock>()
                .Count(tb => tb.Tag is "number"));
        };
        _vm.CopyRequested += DoCopy;
        _vm.CloseRequested += Close;
        _vm.PinRequested += DoPin;

        Root.MouseLeftButtonDown += Root_MouseDown;
        Root.MouseMove += Root_MouseMove;
        Root.MouseLeftButtonUp += Root_MouseUp;
        KeyDown += Window_KeyDown;
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

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            _vm.DpiX = src.CompositionTarget.TransformToDevice.M11;
            _vm.DpiY = src.CompositionTarget.TransformToDevice.M22;
        }
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
    }

    private void Root_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting
            || Root.Tag is not Point start)
        {
            return;
        }

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

    private void TransitionToAnnotating()
    {
        var sel = _vm.SelectionRect;
        Cursor = Cursors.Arrow;
        SizeLabelBorder.Visibility = Visibility.Collapsed;
        DimFull.Visibility = Visibility.Collapsed;

        // Capture clean background pixels for the pixelate tool
        var screenX = (int)((Left + sel.X) * _vm.DpiX);
        var screenY = (int)((Top + sel.Y) * _vm.DpiY);
        var screenW = Math.Max(1, (int)(sel.Width * _vm.DpiX));
        var screenH = Math.Max(1, (int)(sel.Height * _vm.DpiY));
        Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(60);
        var backgroundCapture = _screenCapture.Capture(screenX, screenY, screenW, screenH);
        Visibility = Visibility.Visible;
        _renderer.SetBackground(backgroundCapture, _vm.DpiX, _vm.DpiY);

        LayoutDimStrips(sel);

        AnnotationCanvas.Width = sel.Width;
        AnnotationCanvas.Height = sel.Height;
        Canvas.SetLeft(AnnotationCanvas, sel.X);
        Canvas.SetTop(AnnotationCanvas, sel.Y);
        AnnotationCanvas.Visibility = Visibility.Visible;
        AnnotationCanvas.Cursor = Cursors.Cross;

        AnnotationCanvas.MouseLeftButtonDown += Annot_Down;
        AnnotationCanvas.MouseMove += Annot_Move;
        AnnotationCanvas.MouseLeftButtonUp += Annot_Up;

        PositionToolbar(sel);
        PositionActionBar(sel);
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

    private void PositionToolbar(Rect sel)
    {
        AnnotToolbar.Visibility = Visibility.Visible;
        AnnotToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = AnnotToolbar.DesiredSize;

        var left = sel.Right + 8;
        var top = sel.Top;
        if (left + sz.Width > Width)
        {
            left = sel.Left - sz.Width - 8;
        }

        top = Math.Max(0, Math.Min(top, Height - sz.Height));

        Canvas.SetLeft(AnnotToolbar, left);
        Canvas.SetTop(AnnotToolbar, top);
    }

    private void PositionActionBar(Rect sel)
    {
        ActionBar.Visibility = Visibility.Visible;
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = ActionBar.DesiredSize;

        var left = sel.Left + (sel.Width - sz.Width) / 2;
        var top = sel.Bottom + 8;
        if (top + sz.Height > Height)
        {
            top = sel.Top - sz.Height - 8;
        }

        left = Math.Max(0, Math.Min(left, Width - sz.Width));
        top = Math.Max(0, top);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private void Annot_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(AnnotationCanvas);
        _vm.BeginGroup();
        if (_vm.SelectedTool == AnnotationTool.Text)
        {
            _renderer.PlaceTextBox(p);
            _vm.CommitGroup();
            return;
        }

        if (_vm.SelectedTool == AnnotationTool.Number)
        {
            _renderer.PlaceNumberLabel(p);
            _vm.CommitGroup();
            return;
        }

        _vm.BeginDrawing(p);
        AnnotationCanvas.CaptureMouse();
        _renderer.BeginShape(p);
    }

    private void Annot_Move(object sender, MouseEventArgs e)
    {
        if (!_vm.IsDragging)
        {
            return;
        }

        var p = e.GetPosition(AnnotationCanvas);
        _vm.UpdateDrawing(p);
        _renderer.UpdateShape(p);
    }

    private void Annot_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragging)
        {
            return;
        }

        var p = e.GetPosition(AnnotationCanvas);
        _vm.UpdateDrawing(p);
        AnnotationCanvas.ReleaseMouseCapture();
        _renderer.CommitShape(p);
        _vm.CommitDrawing();
        _vm.CommitGroup();
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag })
        {
            _vm.SelectedTool = Enum.Parse<AnnotationTool>(tag);
            AnnotationCanvas.Cursor = _vm.SelectedTool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
        }
    }

    private void ColorBtn_Click(object sender, RoutedEventArgs e)
    {
        var cur = _vm.ActiveColor;
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(cur.A, cur.R, cur.G, cur.B),
            FullOpen = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            _vm.ActiveColor = Color.FromArgb(c.A, c.R, c.G, c.B);
            ColorDot.Fill = _vm.ActiveBrush;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => DoCopy();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Record_Click(object sender, RoutedEventArgs e)
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
        _recordingBorder = new RecordingBorderWindow(regionRect.Left, regionRect.Top, regionRect.Width, regionRect.Height);
        _recordingBorder.Show();

        _recordingHud = new RecordingHudWindow(_recorder, path, _loggerFactory.CreateLogger<RecordingHudWindow>(), regionRect, _userSettings, _processService);
        _recordingHud.StopCompleted += () => Dispatcher.Invoke(() =>
        {
            _recordingBorder?.Close();
            _recordingBorder = null;
            _recordingHud = null;
            Close();
        });
        _recordingHud.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }

        _recordingHud?.Close();
        _recordingHud = null;
        _recordingBorder?.Close();
        _recordingBorder = null;

        base.OnClosed(e);
    }

    private void DoCopy()
    {
        var final = ComposeBitmap();
        System.Windows.Clipboard.SetImage(final);

        if (_userSettings.Current.AutoSaveScreenshots)
        {
            var saveDir = _userSettings.Current.ScreenshotSavePath;
            _fileSystem.CreateDirectory(saveDir);
            var savePath = _fileSystem.CombinePath(saveDir, $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            using var fs = _fileSystem.OpenWrite(savePath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(final));
            encoder.Save(fs);
        }

        Close();
    }

    private void DoPin()
    {
        var bitmap = ComposeBitmap();
        var pinned = new PinnedScreenshotWindow(bitmap);
        pinned.Show();
        Close();
    }

    /// <summary>
    /// Captures the selected screen region, composites the annotation layer on top,
    /// and returns the result as a <see cref="RenderTargetBitmap"/>.
    /// Shared by <see cref="DoCopy"/> and <see cref="DoPin"/>.
    /// </summary>
    private RenderTargetBitmap ComposeBitmap()
    {
        var sel = _vm.SelectionRect;
        var screenX = (int)((Left + sel.X) * _vm.DpiX);
        var screenY = (int)((Top + sel.Y) * _vm.DpiY);
        var screenW = (int)(sel.Width * _vm.DpiX);
        var screenH = (int)(sel.Height * _vm.DpiY);

        Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(60);
        var screenBmp = _screenCapture.Capture(screenX, screenY, screenW, screenH);
        Visibility = Visibility.Visible;

        var annotRtb = new RenderTargetBitmap(screenW, screenH, 96 * _vm.DpiX, 96 * _vm.DpiY, PixelFormats.Pbgra32);
        annotRtb.Render(AnnotationCanvas);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, screenBmp.PixelWidth, screenBmp.PixelHeight);
            dc.DrawImage(screenBmp, r);
            dc.DrawImage(annotRtb, r);
        }

        var final = new RenderTargetBitmap(screenBmp.PixelWidth, screenBmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        final.Render(dv);
        return final;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _vm.CurrentPhase == OverlayViewModel.Phase.Annotating:
                DoCopy();
                break;
            case Key.Z when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _vm.CurrentPhase == OverlayViewModel.Phase.Annotating:
                _vm.UndoCommand.Execute(null);
                break;
            case Key.Y when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _vm.CurrentPhase == OverlayViewModel.Phase.Annotating:
                _vm.RedoCommand.Execute(null);
                break;
        }
    }
}
