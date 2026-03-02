using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Size = System.Windows.Size;

namespace SnippingTool;

public partial class PreviewWindow : Window
{
    private readonly PreviewViewModel _vm;
    private AnnotationCanvasRenderer _renderer = null!;

    public PreviewWindow(PreviewViewModel vm, BitmapSource bitmap, ILoggerFactory loggerFactory, System.Windows.Rect snipScreenRect = default)
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
        _vm.CopyRequested += () => Clipboard.SetImage(RenderComposite());
        _vm.SaveRequested += DoSave;
        _vm.CloseRequested += Close;

        _renderer = new AnnotationCanvasRenderer(AnnotationCanvas, _vm, el => _vm.TrackElement(el), loggerFactory.CreateLogger<AnnotationCanvasRenderer>());

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
        const double Gap = 8;
        var left = snip.Left;
        var top = snip.Bottom + Gap;
        if (left + Width > workArea.Right)
        {
            left = workArea.Right - Width;
        }

        left = Math.Max(workArea.Left, left);
        if (top + Height > workArea.Bottom)
        {
            top = snip.Top - Height - Gap;
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
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C:
                    _vm.CopyCommand.Execute(null);
                    break;
                case Key.S:
                    _vm.SaveCommand.Execute(null);
                    break;
                case Key.Z:
                    _vm.UndoCommand.Execute(null);
                    break;
                case Key.Y:
                    _vm.RedoCommand.Execute(null);
                    break;
            }
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            _vm.SelectedTool = Enum.Parse<AnnotationTool>(tag);
        }
    }

    private void Color_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        _vm.SetColorFromTag(border.Tag as string);
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
            _vm.SetStrokeThicknessFromText(item.Content?.ToString()?.Split(' ')[0]);
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
            _renderer.PlaceTextBox(pt);
            _vm.CommitDrawing();
            _vm.CommitGroup();
            return;
        }

        if (_vm.SelectedTool == AnnotationTool.Number)
        {
            _renderer.PlaceNumberLabel(pt);
            _vm.CommitDrawing();
            _vm.CommitGroup();
            return;
        }

        _vm.BeginDrawing(pt);
        AnnotationCanvas.CaptureMouse();
        _renderer.BeginShape(pt);
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.IsDragging)
        {
            return;
        }

        var current = e.GetPosition(AnnotationCanvas);
        _vm.UpdateDrawing(current);
        _renderer.UpdateShape(current);
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
        _renderer.CommitShape(current);
        _vm.CommitDrawing();
        _vm.CommitGroup();
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

