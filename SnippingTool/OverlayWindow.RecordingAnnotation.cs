using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services.Messaging;
using Cursors = System.Windows.Input.Cursors;

namespace SnippingTool;

public partial class OverlayWindow
{
    private void InitializeRecordingAnnotationSurface(Rect selectionRect)
    {
        RecordingAnnotationCanvas.Width = selectionRect.Width;
        RecordingAnnotationCanvas.Height = selectionRect.Height;
        Canvas.SetLeft(RecordingAnnotationCanvas, selectionRect.X);
        Canvas.SetTop(RecordingAnnotationCanvas, selectionRect.Y);
        RecordingAnnotationCanvas.Visibility = Visibility.Visible;
        RecordingAnnotationCanvas.Cursor = GetRecordingAnnotationCursor();
        UpdateRecordingAnnotationStateFromCanvas();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, PreWarmRecordingAnnotationRenderer);
    }

    private void PreWarmRecordingAnnotationRenderer()
    {
        // Adding then immediately removing invisible shape elements forces WPF and the JIT to
        // compile the rendering paths for common shape types before the user draws, preventing
        // a visible hitch on the very first stroke.
        UIElement[] warmupElements =
        [
            new Polyline { Stroke = Brushes.Transparent, IsHitTestVisible = false },
            new Line { Stroke = Brushes.Transparent, IsHitTestVisible = false },
            new System.Windows.Shapes.Rectangle { Stroke = Brushes.Transparent, IsHitTestVisible = false },
            new Polygon { Stroke = Brushes.Transparent, IsHitTestVisible = false },
            new Ellipse { Stroke = Brushes.Transparent, IsHitTestVisible = false },
        ];

        foreach (var element in warmupElements)
        {
            RecordingAnnotationCanvas.Children.Add(element);
        }

        foreach (var element in warmupElements)
        {
            RecordingAnnotationCanvas.Children.Remove(element);
        }
    }

    private void HideRecordingAnnotationSurface()
    {
        if (_recordingAnnotationViewModel.ClearCommand.CanExecute(null))
        {
            _recordingAnnotationViewModel.ClearCommand.Execute(null);
        }

        SetRecordingAnnotationInputArmed(false, force: true);
        RecordingAnnotationCanvas.Visibility = Visibility.Collapsed;
        RecordingAnnotationCanvas.Width = 0;
        RecordingAnnotationCanvas.Height = 0;
    }

    private bool ToggleRecordingAnnotationInput()
    {
        var isInputArmed = !_recordingAnnotationViewModel.IsInputArmed;
        SetRecordingAnnotationInputArmed(isInputArmed);
        Visibility = Visibility.Visible;
        EnterRecordingOverlayMode(_vm.SelectionRect);
        _logger.LogDebug(
            "ToggleRecordingAnnotationInput: keeping overlay visible while annotation input is {Mode}",
            _recordingAnnotationViewModel.IsInputArmed ? "armed" : "disarmed");
        return _recordingAnnotationViewModel.IsInputArmed;
    }

    private void SetRecordingAnnotationInputArmed(bool isInputArmed, bool force = false)
    {
        if (!force && _recordingAnnotationViewModel.IsInputArmed == isInputArmed)
        {
            return;
        }

        if (!isInputArmed && !force && HasActiveRecordingEditor())
        {
            _logger.LogInformation("Recording annotation input remains armed because an editor is active");
            return;
        }

        if (!isInputArmed)
        {
            CancelCurrentRecordingShape();
        }

        _recordingAnnotationViewModel.SetInputArmed(isInputArmed);
        RecordingAnnotationCanvas.Cursor = GetRecordingAnnotationCursor();

        if (_recordingAnnotationViewModel.IsInputArmed)
        {
            Activate();
            Focus();
        }

        _logger.LogInformation("Recording annotation input mode changed: {IsInputArmed}", _recordingAnnotationViewModel.IsInputArmed);
    }

    private System.Windows.Input.Cursor GetRecordingAnnotationCursor()
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return Cursors.Arrow;
        }

        return _recordingAnnotationViewModel.SelectedTool == AnnotationTool.Text
            ? Cursors.IBeam
            : Cursors.Pen;
    }

    private void RecordingAnnot_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return;
        }

        _recordingInteractionController.HandlePointerDown(e.GetPosition(RecordingAnnotationCanvas));
        e.Handled = true;
    }

    private void RecordingAnnot_Move(object sender, MouseEventArgs e)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return;
        }

        _recordingInteractionController.HandlePointerMove(e.GetPosition(RecordingAnnotationCanvas));
    }

    private void RecordingAnnot_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingAnnotationViewModel.IsInputArmed)
        {
            return;
        }

        _recordingInteractionController.HandlePointerUp(e.GetPosition(RecordingAnnotationCanvas));
        e.Handled = true;
    }

    private void CancelCurrentRecordingShape()
    {
        _recordingInteractionController.Cancel();
    }

    private void HandleRecordingClearRequested()
    {
        CancelCurrentRecordingShape();
        RecordingAnnotationCanvas.Children.Clear();
        UpdateRecordingAnnotationStateFromCanvas();
    }

    private ValueTask HandleRecordingUndoGroupAsync(UndoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            RecordingAnnotationCanvas.Children.Remove(element);
        }

        UpdateRecordingAnnotationStateFromCanvas();
        return ValueTask.CompletedTask;
    }

    private ValueTask HandleRecordingRedoGroupAsync(RedoGroupMessage message)
    {
        foreach (var element in message.Elements.OfType<UIElement>())
        {
            RecordingAnnotationCanvas.Children.Add(element);
        }

        UpdateRecordingAnnotationStateFromCanvas();
        return ValueTask.CompletedTask;
    }

    private void UpdateRecordingAnnotationStateFromCanvas()
    {
        var numberCount = RecordingAnnotationCanvas.Children
            .OfType<FrameworkElement>()
            .Count(element => element.Tag is "number");
        _recordingAnnotationViewModel.SyncAnnotationState(RecordingAnnotationCanvas.Children.Count > 0, numberCount);
    }

    private bool HasActiveRecordingEditor() => RecordingAnnotationCanvas.Children.OfType<TextBox>().Any();

    private BitmapSource? CaptureLiveRecordingBlurSource(BlurShapeParameters parameters)
    {
        var selectionRect = _vm.SelectionRect;
        var captureBounds = CalculateRecordingCaptureBounds(
            new Rect(Left + selectionRect.Left, Top + selectionRect.Top, selectionRect.Width, selectionRect.Height),
            parameters,
            _vm.DpiX,
            _vm.DpiY);

        var previousVisibility = Visibility;
        try
        {
            Visibility = Visibility.Hidden;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var bitmap = _screenCapture.Capture(captureBounds.X, captureBounds.Y, captureBounds.Width, captureBounds.Height);
            if (bitmap.CanFreeze)
            {
                bitmap.Freeze();
            }

            return bitmap;
        }
        finally
        {
            Visibility = previousVisibility;
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            EnterRecordingOverlayMode(selectionRect);
        }
    }

    internal static Int32Rect CalculateRecordingCaptureBounds(Rect windowBounds, BlurShapeParameters parameters, double dpiX, double dpiY)
    {
        return new Int32Rect(
            (int)Math.Round((windowBounds.Left + parameters.Left) * dpiX),
            (int)Math.Round((windowBounds.Top + parameters.Top) * dpiY),
            Math.Max(1, (int)Math.Round(parameters.Width * dpiX)),
            Math.Max(1, (int)Math.Round(parameters.Height * dpiY)));
    }
}
