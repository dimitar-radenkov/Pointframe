using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using SnippingTool.Models;
using SnippingTool.ViewModels;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace SnippingTool;

public partial class PreviewWindow : Window
{
    private readonly PreviewViewModel _vm;

    private int _numberCounter;

    private Line? _arrowLine;
    private Polyline? _arrowHead;
    private System.Windows.Shapes.Rectangle? _currentRect;
    private Line? _currentLine;
    private Ellipse? _currentEllipse;
    private Polyline? _currentPen;

    public PreviewWindow(PreviewViewModel vm, BitmapSource bitmap, System.Windows.Rect snipScreenRect = default)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;

        _vm.UndoApplied += group =>
        {
            foreach (var el in group.Cast<UIElement>())
            {
                AnnotationCanvas.Children.Remove(el);
            }

            _numberCounter = AnnotationCanvas.Children
                .OfType<TextBlock>()
                .Count(tb => tb.Tag is "number");
        };
        _vm.RedoApplied += group =>
        {
            foreach (var el in group.Cast<UIElement>())
            {
                AnnotationCanvas.Children.Add(el);
            }

            _numberCounter = AnnotationCanvas.Children
                .OfType<TextBlock>()
                .Count(tb => tb.Tag is "number");
        };
        _vm.CopyRequested += () => Clipboard.SetImage(RenderComposite());
        _vm.SaveRequested += DoSave;
        _vm.CloseRequested += Close;

        SnipImage.Source = bitmap;
        AnnotationCanvas.Width = bitmap.PixelWidth;
        AnnotationCanvas.Height = bitmap.PixelHeight;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(bitmap.PixelWidth + 16, workArea.Width - 40);
        Height = Math.Min(bitmap.PixelHeight + 64, workArea.Height - 40);

        if (!snipScreenRect.IsEmpty)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            PositionNearSnip(snipScreenRect, workArea);
        }
    }

    private void PositionNearSnip(System.Windows.Rect snip, System.Windows.Rect workArea)
    {
        const double gap = 8;
        var left = snip.Left;
        var top = snip.Bottom + gap;
        if (left + Width > workArea.Right)
        {
            left = workArea.Right - Width;
        }

        left = Math.Max(workArea.Left, left);
        if (top + Height > workArea.Bottom)
        {
            top = snip.Top - Height - gap;
        }

        top = Math.Max(workArea.Top, top);
        Left = left;
        Top = top;
    }

    private void Toolbar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C: _vm.CopyCommand.Execute(null); break;
                case Key.S: _vm.SaveCommand.Execute(null); break;
                case Key.Z: _vm.UndoCommand.Execute(null); break;
                case Key.Y: _vm.RedoCommand.Execute(null); break;
            }
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
            _vm.SelectedTool = Enum.Parse<AnnotationTool>(tag);
    }

    private void Color_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        _vm.ActiveColor = border.Tag switch
        {
            "Red" => Colors.Red,
            "Blue" => Colors.DodgerBlue,
            "Black" => Color.FromRgb(0x1A, 0x1A, 0x1A),
            "Green" => Color.FromRgb(0x22, 0xA4, 0x22),
            "Orange" => Colors.Orange,
            "Purple" => Color.FromRgb(0x8B, 0x2B, 0xE2),
            "White" => Colors.White,
            "Pink" => Colors.HotPink,
            _ => Colors.Red
        };

        ColorIndicator.Fill = _vm.ActiveBrush;
        ColorPopup.IsOpen = false;
    }

    private void ColorPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = !ColorPopup.IsOpen;
    }

    private void ColorMoreBtn_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = false;
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
            ColorIndicator.Fill = _vm.ActiveBrush;
        }
    }

    private void StrokeThickness_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StrokeThickness.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString()?.Split(' ')[0];
            if (double.TryParse(text, out var t))
                _vm.StrokeThickness = t;
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _vm.UndoCommand.Execute(null);
    private void Redo_Click(object sender, RoutedEventArgs e) => _vm.RedoCommand.Execute(null);

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(AnnotationCanvas);
        _vm.BeginGroup();

        if (_vm.SelectedTool == AnnotationTool.Text)
        {
            PlaceTextBox(pt);
            _vm.CommitDrawing();
            _vm.CommitGroup();
            return;
        }

        if (_vm.SelectedTool == AnnotationTool.Number)
        {
            PlaceNumberLabel(pt);
            _vm.CommitDrawing();
            _vm.CommitGroup();
            return;
        }

        _vm.BeginDrawing(pt);
        AnnotationCanvas.CaptureMouse();

        var brush = new SolidColorBrush(_vm.ActiveColor);
        var thick = _vm.StrokeThickness;

        switch (_vm.SelectedTool)
        {
            case AnnotationTool.Arrow:
                _arrowLine = new Line { X1 = pt.X, Y1 = pt.Y, X2 = pt.X, Y2 = pt.Y, Stroke = brush, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _arrowHead = new Polyline { Stroke = brush, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round };
                AddToCanvas(_arrowLine);
                AddToCanvas(_arrowHead);
                break;
            case AnnotationTool.Rectangle:
                _currentRect = new System.Windows.Shapes.Rectangle { Stroke = brush, StrokeThickness = thick, Fill = Brushes.Transparent };
                Canvas.SetLeft(_currentRect, pt.X);
                Canvas.SetTop(_currentRect, pt.Y);
                AddToCanvas(_currentRect);
                break;
            case AnnotationTool.Highlight:
                _currentRect = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(80, 255, 255, 0)), Stroke = Brushes.Transparent };
                Canvas.SetLeft(_currentRect, pt.X);
                Canvas.SetTop(_currentRect, pt.Y);
                AddToCanvas(_currentRect);
                break;
            case AnnotationTool.Pen:
                _currentPen = new Polyline { Stroke = brush, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _currentPen.Points.Add(pt);
                AddToCanvas(_currentPen);
                break;
            case AnnotationTool.Line:
                _currentLine = new Line { X1 = pt.X, Y1 = pt.Y, X2 = pt.X, Y2 = pt.Y, Stroke = brush, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                AddToCanvas(_currentLine);
                break;
            case AnnotationTool.Circle:
                _currentEllipse = new Ellipse { Stroke = brush, StrokeThickness = thick, Fill = Brushes.Transparent };
                Canvas.SetLeft(_currentEllipse, pt.X);
                Canvas.SetTop(_currentEllipse, pt.Y);
                AddToCanvas(_currentEllipse);
                break;
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.IsDragging)
        {
            return;
        }

        var current = e.GetPosition(AnnotationCanvas);
        _vm.UpdateDrawing(current);

        var @params = _vm.TryGetShapeParameters();
        if (@params == null)
        {
            return;
        }

        switch (@params)
        {
            case ArrowShapeParameters arr when _arrowLine != null && _arrowHead != null:
                _arrowLine.X2 = arr.P2.X;
                _arrowLine.Y2 = arr.P2.Y;
                _arrowHead.Points.Clear();
                foreach (var pt in arr.ArrowHead)
                {
                    _arrowHead.Points.Add(pt);
                }

                break;
            case RectShapeParameters rect when _currentRect != null:
                Canvas.SetLeft(_currentRect, rect.Left);
                Canvas.SetTop(_currentRect, rect.Top);
                _currentRect.Width = rect.Width;
                _currentRect.Height = rect.Height;
                break;
            case EllipseShapeParameters ellipse when _currentEllipse != null:
                Canvas.SetLeft(_currentEllipse, ellipse.Left);
                Canvas.SetTop(_currentEllipse, ellipse.Top);
                _currentEllipse.Width = ellipse.Width;
                _currentEllipse.Height = ellipse.Height;
                break;
            case LineShapeParameters line when _currentLine != null:
                _currentLine.X2 = line.P2.X;
                _currentLine.Y2 = line.P2.Y;
                break;
            case PenShapeParameters when _currentPen != null:
                _currentPen.Points.Add(current);
                break;
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsDragging)
        {
            return;
        }

        var current = e.GetPosition(AnnotationCanvas);
        _vm.UpdateDrawing(current);
        AnnotationCanvas.ReleaseMouseCapture();

        // Final arrow head update
        if (_vm.SelectedTool == AnnotationTool.Arrow && _arrowLine != null && _arrowHead != null)
        {
            var head = _vm.TryGetShapeParameters() as ArrowShapeParameters;
            if (head != null)
            {
                _arrowHead.Points.Clear();
                foreach (var pt in head.ArrowHead)
                {
                    _arrowHead.Points.Add(pt);
                }
            }
        }

        _vm.CommitDrawing();

        _arrowLine = null; _arrowHead = null;
        _currentRect = null; _currentLine = null;
        _currentEllipse = null; _currentPen = null;

        _vm.CommitGroup();
    }

    private void AddToCanvas(UIElement element)
    {
        AnnotationCanvas.Children.Add(element);
        _vm.TrackElement(element);
    }

    private void PlaceTextBox(Point position)
    {
        var tb = new TextBox
        {
            FontSize = 16,
            Foreground = new SolidColorBrush(_vm.ActiveColor),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            MinWidth = 80,
            AcceptsReturn = false
        };
        tb.LostFocus += (_, _) => { tb.IsReadOnly = true; tb.BorderThickness = new Thickness(0); tb.Cursor = Cursors.Arrow; };
        Canvas.SetLeft(tb, position.X);
        Canvas.SetTop(tb, position.Y);
        AddToCanvas(tb);
        tb.Focus();
    }

    private void PlaceNumberLabel(Point position)
    {
        _numberCounter++;
        var tb = new TextBlock
        {
            Text = $"({_numberCounter})",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(_vm.ActiveColor),
            Tag = "number"
        };
        Canvas.SetLeft(tb, position.X);
        Canvas.SetTop(tb, position.Y);
        AddToCanvas(tb);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => _vm.CopyCommand.Execute(null);
    private void Save_Click(object sender, RoutedEventArgs e) => _vm.SaveCommand.Execute(null);

    private void DoSave()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Screenshot",
            Filter = "PNG Image|*.png|JPEG Image|*.jpg",
            DefaultExt = ".png",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        var rtb = RenderComposite();
        BitmapEncoder encoder = dlg.FilterIndex == 2
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = new System.IO.FileStream(dlg.FileName, System.IO.FileMode.Create);
        encoder.Save(fs);
    }

    private RenderTargetBitmap RenderComposite()
    {
        RootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RootGrid.Arrange(new Rect(RootGrid.DesiredSize));
        var rtb = new RenderTargetBitmap(
            (int)AnnotationCanvas.Width, (int)AnnotationCanvas.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(RootGrid);
        rtb.Freeze();
        return rtb;
    }
}

