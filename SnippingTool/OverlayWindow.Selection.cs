using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SnippingTool.ViewModels;

namespace SnippingTool;

public partial class OverlayWindow
{
    private const int LoupeSize = 120;
    private const int LoupeZoom = 4;
    private const int LoupeOffset = 20;

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentPhase != OverlayViewModel.Phase.Selecting)
        {
            return;
        }

        var start = e.GetPosition(Root);
        Root.Tag = start;
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

        var selectionRect = new Rect(x, y, w, h);
        var selectionScreenBoundsPixels = GetScreenPixelBounds(selectionRect);
        _vm.CommitSelection(selectionRect, selectionScreenBoundsPixels);
        TransitionToAnnotating();
    }

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
        var captureBounds = _vm.SelectionScreenBoundsPixels.Width > 0 && _vm.SelectionScreenBoundsPixels.Height > 0
            ? _vm.SelectionScreenBoundsPixels
            : GetScreenPixelBounds(sel);
        CloseSelectionBackdropWindows();
        Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(60);
        var backgroundCapture = _screenCapture.Capture(
            captureBounds.X,
            captureBounds.Y,
            captureBounds.Width,
            captureBounds.Height);
        Visibility = Visibility.Visible;
        _screenSnapshot = null;

        var pixelScaleX = sel.Width > 0d ? captureBounds.Width / sel.Width : _vm.DpiX;
        var pixelScaleY = sel.Height > 0d ? captureBounds.Height / sel.Height : _vm.DpiY;

        _logger.LogDebug(
            "Selection mapped to screen pixels: localDips={LocalX},{LocalY},{LocalW},{LocalH} screenPx={ScreenX},{ScreenY},{ScreenW},{ScreenH} scale={ScaleX},{ScaleY}",
            sel.X,
            sel.Y,
            sel.Width,
            sel.Height,
            captureBounds.X,
            captureBounds.Y,
            captureBounds.Width,
            captureBounds.Height,
            pixelScaleX,
            pixelScaleY);

        EnterAnnotatingSession(sel, backgroundCapture, pixelScaleX, pixelScaleY, allowRecording: true);
    }

    private Int32Rect GetScreenPixelBounds(Rect localRect)
    {
        var topLeft = PointToScreen(new Point(localRect.Left, localRect.Top));
        var bottomRight = PointToScreen(new Point(localRect.Right, localRect.Bottom));

        var x = (int)Math.Round(Math.Min(topLeft.X, bottomRight.X));
        var y = (int)Math.Round(Math.Min(topLeft.Y, bottomRight.Y));
        var width = Math.Max(1, (int)Math.Round(Math.Abs(bottomRight.X - topLeft.X)));
        var height = Math.Max(1, (int)Math.Round(Math.Abs(bottomRight.Y - topLeft.Y)));

        return new Int32Rect(x, y, width, height);
    }
}
