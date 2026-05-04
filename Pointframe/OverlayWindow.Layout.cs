using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Pointframe.Services;
using Cursors = System.Windows.Input.Cursors;
using Forms = System.Windows.Forms;

namespace Pointframe;

public partial class OverlayWindow
{
    private void InitializeFromOpenedImage(BitmapSource openedImage)
    {
        _selectionSessionMode = SelectionSessionMode.OpenedImage;
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
            allowRecording: false,
            SelectionSessionMode.OpenedImage);
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

    private void EnterAnnotatingSession(
        Rect selectionRect,
        BitmapSource backgroundBitmap,
        double pixelScaleX,
        double pixelScaleY,
        bool allowRecording,
        SelectionSessionMode sessionMode)
    {
        _logger.LogDebug(
            "EnterAnnotatingSession: selectionRect={SX:F1},{SY:F1},{SW:F1},{SH:F1} windowSize={WW:F1}×{WH:F1} mode={Mode} allowRecording={AllowRecording}",
            selectionRect.X,
            selectionRect.Y,
            selectionRect.Width,
            selectionRect.Height,
            Width,
            Height,
            sessionMode,
            allowRecording);

        _selectionSessionMode = sessionMode;
        RecordBtn.Visibility = allowRecording ? Visibility.Visible : Visibility.Collapsed;
        CompactRecordBtn.Visibility = allowRecording ? Visibility.Visible : Visibility.Collapsed;

        var hostBounds = selectionRect;
        if (allowRecording && sessionMode == SelectionSessionMode.Region)
        {
            selectionRect = RehostAnnotatingOverlay(selectionRect, out hostBounds);
        }

        _vm.InitializeAnnotatingSession(selectionRect, pixelScaleX, pixelScaleY);

        Cursor = Cursors.Arrow;
        if (allowRecording && _annotatingMonitorSnapshot is not null)
        {
            ShowAnnotatingBackdropWindows();

            var backgroundCrop = CreateAnnotatingBackdropCrop(hostBounds, pixelScaleX, pixelScaleY, _annotatingMonitorSnapshot);
            ScreenSnapshot.Source = backgroundCrop;
            ScreenSnapshot.Width = Width;
            ScreenSnapshot.Height = Height;
            Canvas.SetLeft(ScreenSnapshot, 0d);
            Canvas.SetTop(ScreenSnapshot, 0d);
            ScreenSnapshot.Visibility = Visibility.Visible;

            if (sessionMode == SelectionSessionMode.Region)
            {
                LayoutDimStrips(selectionRect);
            }
            else
            {
                HideDimStrips();
            }
        }
        else
        {
            ScreenSnapshot.Visibility = Visibility.Collapsed;
            HideDimStrips();
        }

        Canvas.SetLeft(SelectionBorder, selectionRect.X);
        Canvas.SetTop(SelectionBorder, selectionRect.Y);
        SelectionBorder.Width = selectionRect.Width;
        SelectionBorder.Height = selectionRect.Height;
        SelectionBorder.Visibility = sessionMode == SelectionSessionMode.FullScreen
            ? Visibility.Collapsed
            : Visibility.Visible;

        _renderer.SetBackground(backgroundBitmap, pixelScaleX, pixelScaleY);

        AnnotationCanvas.Width = selectionRect.Width;
        AnnotationCanvas.Height = selectionRect.Height;
        Canvas.SetLeft(AnnotationCanvas, selectionRect.X);
        Canvas.SetTop(AnnotationCanvas, selectionRect.Y);
        AnnotationCanvas.Background = new ImageBrush(backgroundBitmap)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        AnnotationCanvas.Visibility = Visibility.Visible;
        AnnotationCanvas.Cursor = Cursors.Cross;

        AnnotationCanvas.MouseLeftButtonDown += Annot_Down;
        AnnotationCanvas.MouseMove += Annot_Move;
        AnnotationCanvas.MouseLeftButtonUp += Annot_Up;

        PositionToolbars(selectionRect, sessionMode);
    }

    private Rect RehostAnnotatingOverlay(Rect selectionRect, out Rect hostBounds)
    {
        var overlayBounds = new Rect(0, 0, Width, Height);
        var toolSize = MeasureFloatingElement(AnnotToolbar);
        var fullActionSize = MeasureFloatingElement(ActionBar);
        var compactActionSize = MeasureFloatingElement(CompactActionBar);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
            new Size(Width, Height),
            toolSize,
            fullActionSize,
            compactActionSize,
            OverlayToolbarLayoutMode.SelectionAdjacent);

        hostBounds = selectionRect;
        hostBounds.Union(layout.ToolBounds);
        hostBounds.Union(layout.ActionBounds);
        hostBounds.Inflate(16d, 16d);

        hostBounds = new Rect(
            Math.Max(overlayBounds.Left, hostBounds.Left),
            Math.Max(overlayBounds.Top, hostBounds.Top),
            Math.Min(overlayBounds.Right, hostBounds.Right) - Math.Max(overlayBounds.Left, hostBounds.Left),
            Math.Min(overlayBounds.Bottom, hostBounds.Bottom) - Math.Max(overlayBounds.Top, hostBounds.Top));

        if (hostBounds.Width <= 0d || hostBounds.Height <= 0d)
        {
            _logger.LogWarning(
                "RehostAnnotatingOverlay: computed hostBounds are degenerate ({W:F1}×{H:F1}), falling back to full overlay",
                hostBounds.Width,
                hostBounds.Height);
            hostBounds = overlayBounds;
            return selectionRect;
        }

        var rebasedSelectionRect = new Rect(
            selectionRect.Left - hostBounds.Left,
            selectionRect.Top - hostBounds.Top,
            selectionRect.Width,
            selectionRect.Height);

        _logger.LogDebug(
            "Rehost annotating overlay: oldWindow={OldLeft},{OldTop},{OldWidth},{OldHeight} newWindow={NewLeft},{NewTop},{NewWidth},{NewHeight} rebasedSelection={SelLeft},{SelTop},{SelWidth},{SelHeight}",
            Left,
            Top,
            Width,
            Height,
            Left + hostBounds.Left,
            Top + hostBounds.Top,
            hostBounds.Width,
            hostBounds.Height,
            rebasedSelectionRect.Left,
            rebasedSelectionRect.Top,
            rebasedSelectionRect.Width,
            rebasedSelectionRect.Height);

        Left += hostBounds.Left;
        Top += hostBounds.Top;
        Width = hostBounds.Width;
        Height = hostBounds.Height;

        return rebasedSelectionRect;
    }

    private void LayoutDimStrips(Rect selectionRect)
    {
        var rawDimTopH = selectionRect.Top;
        var rawDimBottomH = Height - selectionRect.Bottom;
        var rawDimLeftW = selectionRect.Left;
        var rawDimRightW = Width - selectionRect.Right;

        if (rawDimTopH < 0d || rawDimBottomH < 0d || rawDimLeftW < 0d || rawDimRightW < 0d)
        {
            _logger.LogWarning(
                "LayoutDimStrips: selection overflows window — selectionRect={SX:F1},{SY:F1},{SW:F1},{SH:F1} windowSize={WW:F1}×{WH:F1} rawStrips T={T:F1} B={B:F1} L={L:F1} R={R:F1}",
                selectionRect.X,
                selectionRect.Y,
                selectionRect.Width,
                selectionRect.Height,
                Width,
                Height,
                rawDimTopH,
                rawDimBottomH,
                rawDimLeftW,
                rawDimRightW);
        }
        else
        {
            _logger.LogDebug(
                "LayoutDimStrips: selectionRect={SX:F1},{SY:F1},{SW:F1},{SH:F1} windowSize={WW:F1}×{WH:F1}",
                selectionRect.X,
                selectionRect.Y,
                selectionRect.Width,
                selectionRect.Height,
                Width,
                Height);
        }

        DimTop.SetValue(Canvas.LeftProperty, 0d);
        DimTop.SetValue(Canvas.TopProperty, 0d);
        DimTop.Width = Width;
        DimTop.Height = Math.Max(0d, selectionRect.Top);
        DimTop.Visibility = Visibility.Visible;

        DimBottom.SetValue(Canvas.LeftProperty, 0d);
        DimBottom.SetValue(Canvas.TopProperty, selectionRect.Bottom);
        DimBottom.Width = Width;
        DimBottom.Height = Math.Max(0d, Height - selectionRect.Bottom);
        DimBottom.Visibility = Visibility.Visible;

        DimLeft.SetValue(Canvas.LeftProperty, 0d);
        DimLeft.SetValue(Canvas.TopProperty, selectionRect.Top);
        DimLeft.Width = Math.Max(0d, selectionRect.Left);
        DimLeft.Height = selectionRect.Height;
        DimLeft.Visibility = Visibility.Visible;

        DimRight.SetValue(Canvas.LeftProperty, selectionRect.Right);
        DimRight.SetValue(Canvas.TopProperty, selectionRect.Top);
        DimRight.Width = Math.Max(0d, Width - selectionRect.Right);
        DimRight.Height = selectionRect.Height;
        DimRight.Visibility = Visibility.Visible;
    }

    private void HideDimStrips()
    {
        DimTop.Visibility = Visibility.Collapsed;
        DimBottom.Visibility = Visibility.Collapsed;
        DimLeft.Visibility = Visibility.Collapsed;
        DimRight.Visibility = Visibility.Collapsed;
    }

    private static BitmapSource CreateAnnotatingBackdropCrop(Rect hostBounds, double pixelScaleX, double pixelScaleY, BitmapSource monitorSnapshot)
    {
        var cropX = Math.Max(0, (int)Math.Round(hostBounds.X * pixelScaleX));
        var cropY = Math.Max(0, (int)Math.Round(hostBounds.Y * pixelScaleY));
        var cropWidth = Math.Max(1, (int)Math.Round(hostBounds.Width * pixelScaleX));
        var cropHeight = Math.Max(1, (int)Math.Round(hostBounds.Height * pixelScaleY));

        cropWidth = Math.Min(cropWidth, monitorSnapshot.PixelWidth - cropX);
        cropHeight = Math.Min(cropHeight, monitorSnapshot.PixelHeight - cropY);

        var croppedBitmap = new CroppedBitmap(monitorSnapshot, new Int32Rect(cropX, cropY, cropWidth, cropHeight));
        croppedBitmap.Freeze();
        return croppedBitmap;
    }

    private void PositionToolbars(Rect selectionRect, SelectionSessionMode sessionMode)
    {
        var toolSize = MeasureFloatingElement(AnnotToolbar);
        var fullActionSize = MeasureFloatingElement(ActionBar);
        var compactActionSize = MeasureFloatingElement(CompactActionBar);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
            new Size(Width, Height),
            toolSize,
            fullActionSize,
            compactActionSize,
            sessionMode == SelectionSessionMode.FullScreen
                ? OverlayToolbarLayoutMode.FullScreen
                : OverlayToolbarLayoutMode.SelectionAdjacent);

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
