using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;
using SnippingTool.Services;
using SnippingTool.ViewModels;

namespace SnippingTool;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly IScreenCaptureService _screenCapture;

    private bool _annotDragging;
    private Point _annotStart;
    private Line? _arrowShaft;
    private Polyline? _arrowHead;
    private Line? _currentLine;
    private System.Windows.Shapes.Rectangle? _currentRect;
    private Ellipse? _currentEllipse;
    private Polyline? _currentPen;

    public OverlayWindow(OverlayViewModel vm, IScreenCaptureService screenCapture)
    {
        _vm = vm;
        _screenCapture = screenCapture;
        InitializeComponent();
        DataContext = _vm;

        _vm.CopyRequested += DoCopy;
        _vm.CloseRequested += Close;

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
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting) return;

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
            || Root.Tag is not Point start) return;

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
        if (ly < 0) ly = y + 4;
        Canvas.SetLeft(SizeLabelBorder, x);
        Canvas.SetTop(SizeLabelBorder, ly);
    }

    private void Root_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting
            || Root.Tag is not Point start) return;

        Root.Tag = null;
        Root.ReleaseMouseCapture();

        var end = e.GetPosition(Root);
        var x = Math.Min(end.X, start.X);
        var y = Math.Min(end.Y, start.Y);
        var w = Math.Abs(end.X - start.X);
        var h = Math.Abs(end.Y - start.Y);

        if (w < 4 || h < 4) { Close(); return; }

        _vm.CommitSelection(new Rect(x, y, w, h));
        TransitionToAnnotating();
    }

    private void TransitionToAnnotating()
    {
        var sel = _vm.SelectionRect;
        Cursor = Cursors.Arrow;
        SizeLabelBorder.Visibility = Visibility.Collapsed;
        DimFull.Visibility = Visibility.Collapsed;
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
        if (left + sz.Width > Width) left = sel.Left - sz.Width - 8;
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
        if (top + sz.Height > Height) top = sel.Top - sz.Height - 8;
        left = Math.Max(0, Math.Min(left, Width - sz.Width));
        top = Math.Max(0, top);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    private void Annot_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(AnnotationCanvas);
        if (_vm.SelectedTool == AnnotationTool.Text) { PlaceTextBox(p); return; }
        _annotStart = p;
        _annotDragging = true;
        AnnotationCanvas.CaptureMouse();
        BeginShape(p);
    }

    private void Annot_Move(object sender, MouseEventArgs e)
    {
        if (!_annotDragging) return;
        UpdateShape(e.GetPosition(AnnotationCanvas));
    }

    private void Annot_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_annotDragging) return;
        _annotDragging = false;
        AnnotationCanvas.ReleaseMouseCapture();
        CommitShape(e.GetPosition(AnnotationCanvas));
    }

    private SolidColorBrush ActiveBrush() => new(_vm.ActiveColor);

    private void BeginShape(Point p)
    {
        var thick = _vm.StrokeThickness;
        switch (_vm.SelectedTool)
        {
            case AnnotationTool.Arrow:
                _arrowShaft = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = ActiveBrush(), StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _arrowHead = new Polyline { Stroke = ActiveBrush(), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeEndLineCap = PenLineCap.Round };
                AnnotationCanvas.Children.Add(_arrowShaft);
                AnnotationCanvas.Children.Add(_arrowHead);
                break;
            case AnnotationTool.Line:
                _currentLine = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = ActiveBrush(), StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                AnnotationCanvas.Children.Add(_currentLine);
                break;
            case AnnotationTool.Highlight:
                var c = _vm.ActiveColor;
                _currentRect = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(100, c.R, c.G, c.B)) };
                Canvas.SetLeft(_currentRect, p.X);
                Canvas.SetTop(_currentRect, p.Y);
                AnnotationCanvas.Children.Add(_currentRect);
                break;
            case AnnotationTool.Pen:
                _currentPen = new Polyline { Stroke = ActiveBrush(), StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _currentPen.Points.Add(p);
                AnnotationCanvas.Children.Add(_currentPen);
                break;
            case AnnotationTool.Circle:
                _currentEllipse = new Ellipse { Stroke = ActiveBrush(), StrokeThickness = thick, Fill = Brushes.Transparent };
                Canvas.SetLeft(_currentEllipse, p.X);
                Canvas.SetTop(_currentEllipse, p.Y);
                AnnotationCanvas.Children.Add(_currentEllipse);
                break;
        }
    }

    private void UpdateShape(Point p)
    {
        switch (_vm.SelectedTool)
        {
            case AnnotationTool.Arrow when _arrowShaft != null && _arrowHead != null:
                _arrowShaft.X2 = p.X; _arrowShaft.Y2 = p.Y;
                RefreshArrowHead(_arrowShaft, _arrowHead);
                break;
            case AnnotationTool.Line when _currentLine != null:
                _currentLine.X2 = p.X; _currentLine.Y2 = p.Y;
                break;
            case AnnotationTool.Highlight when _currentRect != null:
                _currentRect.Width = Math.Abs(p.X - _annotStart.X);
                _currentRect.Height = Math.Abs(p.Y - _annotStart.Y);
                Canvas.SetLeft(_currentRect, Math.Min(p.X, _annotStart.X));
                Canvas.SetTop(_currentRect, Math.Min(p.Y, _annotStart.Y));
                break;
            case AnnotationTool.Pen when _currentPen != null:
                _currentPen.Points.Add(p);
                break;
            case AnnotationTool.Circle when _currentEllipse != null:
                _currentEllipse.Width = Math.Abs(p.X - _annotStart.X);
                _currentEllipse.Height = Math.Abs(p.Y - _annotStart.Y);
                Canvas.SetLeft(_currentEllipse, Math.Min(p.X, _annotStart.X));
                Canvas.SetTop(_currentEllipse, Math.Min(p.Y, _annotStart.Y));
                break;
        }
    }

    private void CommitShape(Point p)
    {
        UpdateShape(p);
        _arrowShaft = null; _arrowHead = null;
        _currentLine = null; _currentRect = null;
        _currentEllipse = null; _currentPen = null;
    }

    private static void RefreshArrowHead(Line shaft, Polyline head)
    {
        const double headLen = 12.0;
        const double headAngle = 25.0 * Math.PI / 180.0;
        var angle = Math.Atan2(shaft.Y2 - shaft.Y1, shaft.X2 - shaft.X1);
        head.Points.Clear();
        head.Points.Add(new Point(shaft.X2 - headLen * Math.Cos(angle + headAngle), shaft.Y2 - headLen * Math.Sin(angle + headAngle)));
        head.Points.Add(new Point(shaft.X2, shaft.Y2));
        head.Points.Add(new Point(shaft.X2 - headLen * Math.Cos(angle - headAngle), shaft.Y2 - headLen * Math.Sin(angle - headAngle)));
    }

    private void PlaceTextBox(Point p)
    {
        var tb = new TextBox
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Foreground = ActiveBrush(),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 60,
            AcceptsReturn = false,
            Padding = new Thickness(2)
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y);
        AnnotationCanvas.Children.Add(tb);
        tb.Focus();
        tb.KeyDown += (_, ke) => { if (ke.Key is Key.Enter or Key.Escape) { FinalizeTextBox(tb); ke.Handled = true; } };
        tb.LostFocus += (_, _) => FinalizeTextBox(tb);
    }

    private void FinalizeTextBox(TextBox tb)
    {
        if (!AnnotationCanvas.Children.Contains(tb)) return;
        if (string.IsNullOrWhiteSpace(tb.Text)) { AnnotationCanvas.Children.Remove(tb); return; }
        var pos = new Point(Canvas.GetLeft(tb), Canvas.GetTop(tb));
        var text = tb.Text;
        var brush = tb.Foreground;
        AnnotationCanvas.Children.Remove(tb);
        var block = new TextBlock { Text = text, Foreground = brush, FontSize = 16, FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(block, pos.X);
        Canvas.SetTop(block, pos.Y);
        AnnotationCanvas.Children.Add(block);
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

    private void DoCopy()
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
        System.Windows.Clipboard.SetImage(final);
        Close();
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
        }
    }
}