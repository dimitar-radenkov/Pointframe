using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SnippingTool.Services;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;

namespace SnippingTool;

public partial class OverlayWindow
{
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

    private void LayoutDimStrips(Rect selectionRect)
    {
        var overlayWidth = Width;
        var overlayHeight = Height;

        DimTop.SetValue(Canvas.LeftProperty, 0d);
        DimTop.SetValue(Canvas.TopProperty, 0d);
        DimTop.Width = overlayWidth;
        DimTop.Height = selectionRect.Top;
        DimTop.Visibility = Visibility.Visible;

        DimBottom.SetValue(Canvas.LeftProperty, 0d);
        DimBottom.SetValue(Canvas.TopProperty, selectionRect.Bottom);
        DimBottom.Width = overlayWidth;
        DimBottom.Height = overlayHeight - selectionRect.Bottom;
        DimBottom.Visibility = Visibility.Visible;

        DimLeft.SetValue(Canvas.LeftProperty, 0d);
        DimLeft.SetValue(Canvas.TopProperty, selectionRect.Top);
        DimLeft.Width = selectionRect.Left;
        DimLeft.Height = selectionRect.Height;
        DimLeft.Visibility = Visibility.Visible;

        DimRight.SetValue(Canvas.LeftProperty, selectionRect.Right);
        DimRight.SetValue(Canvas.TopProperty, selectionRect.Top);
        DimRight.Width = overlayWidth - selectionRect.Right;
        DimRight.Height = selectionRect.Height;
        DimRight.Visibility = Visibility.Visible;
    }

    private void PositionToolbars(Rect selectionRect)
    {
        var toolSize = MeasureFloatingElement(AnnotToolbar);
        var fullActionSize = MeasureFloatingElement(ActionBar);
        var compactActionSize = MeasureFloatingElement(CompactActionBar);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
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
}
