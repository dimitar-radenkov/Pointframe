using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace SnippingTool;

public partial class OverlayWindow
{
    private const double RecordingBorderStrokeThickness = 2d;
    private const double RecordingBorderClearance = 6d;
    private const double RecordingBorderOffset = RecordingBorderStrokeThickness + RecordingBorderClearance;

    private async void Record_Click(object sender, RoutedEventArgs e) => await StartRecordingSessionAsync();

    private async Task StartRecordingSessionAsync()
    {
        var selectionRect = _vm.SelectionRect;
        var screenX = (int)((Left + selectionRect.X) * _vm.DpiX);
        var screenY = (int)((Top + selectionRect.Y) * _vm.DpiY);
        var screenW = (int)(selectionRect.Width * _vm.DpiX);
        var screenH = (int)(selectionRect.Height * _vm.DpiY);

        _logger.LogDebug(
            "StartRecordingSession: overlay DIP Left={OverlayLeft} Top={OverlayTop} DpiX={DpiX} DpiY={DpiY}",
            Left, Top, _vm.DpiX, _vm.DpiY);
        _logger.LogDebug(
            "StartRecordingSession: selection DIP X={SelX} Y={SelY} W={SelW} H={SelH}",
            selectionRect.X, selectionRect.Y, selectionRect.Width, selectionRect.Height);
        _logger.LogDebug(
            "StartRecordingSession: capture physical px X={ScreenX} Y={ScreenY} W={ScreenW} H={ScreenH}",
            screenX, screenY, screenW, screenH);

        var videosDir = _userSettings.Current.RecordingOutputPath;
        _fileSystem.CreateDirectory(videosDir);
        var ext = _userSettings.Current.RecordingFormat == Models.RecordingFormat.Mp4 ? ".mp4" : ".avi";
        var path = _fileSystem.CombinePath(videosDir, $"SnipRec-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");

        try
        {
            EnterRecordingOverlayMode(selectionRect);
            await Task.Run(() => _recorder.Start(screenX, screenY, screenW, screenH, path));
        }
        catch (System.IO.FileNotFoundException ex)
        {
            ExitRecordingOverlayMode(selectionRect);
            _messageBox.ShowWarning(ex.Message, "ffmpeg not found");
            return;
        }

        var regionRect = new Rect(Left + selectionRect.X, Top + selectionRect.Y, selectionRect.Width, selectionRect.Height);
        await Dispatcher.Yield(DispatcherPriority.Background);
        ShowRecordingSessionWindows(selectionRect, regionRect, path);
    }

    private void ShowRecordingSessionWindows(Rect selectionRect, Rect regionRect, string outputPath)
    {
        var borderRect = CalculateRecordingBorderRect(regionRect, RecordingBorderOffset);

        _logger.LogDebug(
            "ShowRecordingSessionWindows: regionRect DIP L={L} T={T} W={W} H={H}",
            regionRect.Left, regionRect.Top, regionRect.Width, regionRect.Height);
        _logger.LogDebug(
            "ShowRecordingSessionWindows: borderRect DIP L={L} T={T} W={W} H={H}",
            borderRect.Left, borderRect.Top, borderRect.Width, borderRect.Height);
        _logger.LogDebug(
            "ShowRecordingSessionWindows: border local DIP L={L} T={T} W={W} H={H}",
            selectionRect.Left - RecordingBorderOffset,
            selectionRect.Top - RecordingBorderOffset,
            selectionRect.Width + (RecordingBorderOffset * 2d),
            selectionRect.Height + (RecordingBorderOffset * 2d));
        InitializeRecordingAnnotationSurface(selectionRect);

        var hudVm = _recordingHudViewModelFactory(_recorder, outputPath);
        hudVm.AttachAnnotationSession(_recordingAnnotationViewModel, ToggleRecordingAnnotationInput);
        ShowRecordingHud(selectionRect, hudVm);
    }

    private void CloseRecordingSessionWindows()
    {
        HideRecordingHud();
        HideRecordingAnnotationSurface();
    }

    private void EnterRecordingOverlayMode(Rect selectionRect)
    {
        _logger.LogDebug(
            "EnterRecordingOverlayMode: border local DIP L={L} T={T} W={W} H={H}",
            selectionRect.Left - RecordingBorderOffset,
            selectionRect.Top - RecordingBorderOffset,
            selectionRect.Width + (RecordingBorderOffset * 2d),
            selectionRect.Height + (RecordingBorderOffset * 2d));

        Root.Background = Brushes.Transparent;
        ScreenSnapshot.Visibility = Visibility.Collapsed;
        DimFull.Visibility = Visibility.Collapsed;
        DimTop.Visibility = Visibility.Collapsed;
        DimBottom.Visibility = Visibility.Collapsed;
        DimLeft.Visibility = Visibility.Collapsed;
        DimRight.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        OcrLassoRect.Visibility = Visibility.Collapsed;
        SizeLabelBorder.Visibility = Visibility.Collapsed;
        LoupeBorder.Visibility = Visibility.Collapsed;
        AnnotationCanvas.Visibility = Visibility.Collapsed;
        RecordingAnnotationCanvas.Visibility = RecordingAnnotationCanvas.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        AnnotToolbar.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Collapsed;
        CompactActionBar.Visibility = Visibility.Collapsed;

        _isRecordingOverlayMode = true;
        PositionRecordingBorder(selectionRect);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private void ExitRecordingOverlayMode(Rect selectionRect)
    {
        _isRecordingOverlayMode = false;
        Root.Background = _interactiveRootBackground;
        HideRecordingBorder();
        HideRecordingHud();
        HideRecordingAnnotationSurface();

        ScreenSnapshot.Visibility = Visibility.Visible;
        DimFull.Visibility = Visibility.Collapsed;
        LayoutDimStrips(selectionRect);

        Canvas.SetLeft(SelectionBorder, selectionRect.X);
        Canvas.SetTop(SelectionBorder, selectionRect.Y);
        SelectionBorder.Width = selectionRect.Width;
        SelectionBorder.Height = selectionRect.Height;
        SelectionBorder.Visibility = Visibility.Visible;

        AnnotationCanvas.Visibility = Visibility.Visible;
        PositionToolbars(selectionRect);
    }

    private void PositionRecordingBorder(Rect selectionRect)
    {
        var borderRect = CalculateRecordingBorderRect(selectionRect, RecordingBorderOffset);

        Canvas.SetLeft(RecordingBorderWhite, borderRect.Left);
        Canvas.SetTop(RecordingBorderWhite, borderRect.Top);
        RecordingBorderWhite.Width = borderRect.Width;
        RecordingBorderWhite.Height = borderRect.Height;
        RecordingBorderWhite.Visibility = Visibility.Visible;

        Canvas.SetLeft(RecordingBorderBlack, borderRect.Left);
        Canvas.SetTop(RecordingBorderBlack, borderRect.Top);
        RecordingBorderBlack.Width = borderRect.Width;
        RecordingBorderBlack.Height = borderRect.Height;
        RecordingBorderBlack.Visibility = Visibility.Visible;
    }

    internal static Rect CalculateRecordingBorderRect(Rect selectionRect, double borderOffset)
    {
        return new Rect(
            selectionRect.Left - borderOffset,
            selectionRect.Top - borderOffset,
            selectionRect.Width + (borderOffset * 2d),
            selectionRect.Height + (borderOffset * 2d));
    }

    private void HideRecordingBorder()
    {
        RecordingBorderWhite.Visibility = Visibility.Collapsed;
        RecordingBorderBlack.Visibility = Visibility.Collapsed;
    }
}
