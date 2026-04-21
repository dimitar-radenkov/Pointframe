using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pointframe.Models;
using Pointframe.ViewModels;
using Cursor = System.Windows.Input.Cursor;

namespace Pointframe.Services;

internal sealed class RecordingAnnotationSurfaceCoordinator
{
    private readonly Canvas _recordingAnnotationCanvas;
    private readonly RecordingSessionGeometry _geometry;
    private readonly RecordingAnnotationViewModel _recordingAnnotationViewModel;

    public RecordingAnnotationSurfaceCoordinator(
        Canvas recordingAnnotationCanvas,
        RecordingSessionGeometry geometry,
        RecordingAnnotationViewModel recordingAnnotationViewModel)
    {
        _recordingAnnotationCanvas = recordingAnnotationCanvas;
        _geometry = geometry;
        _recordingAnnotationViewModel = recordingAnnotationViewModel;
    }

    public void Initialize(Cursor cursor, Action preWarmRenderer, Action<Rect> logAnnotationSurface)
    {
        var captureCanvasRect = _geometry.GetCaptureCanvasRectDips();

        _recordingAnnotationCanvas.Width = captureCanvasRect.Width;
        _recordingAnnotationCanvas.Height = captureCanvasRect.Height;
        Canvas.SetLeft(_recordingAnnotationCanvas, captureCanvasRect.X);
        Canvas.SetTop(_recordingAnnotationCanvas, captureCanvasRect.Y);
        _recordingAnnotationCanvas.Cursor = cursor;
        SyncAnnotationState();
        logAnnotationSurface(captureCanvasRect);
        _recordingAnnotationCanvas.Dispatcher.BeginInvoke(DispatcherPriority.Background, preWarmRenderer);
    }

    public void Hide(Action disarmAnnotationInput)
    {
        if (_recordingAnnotationViewModel.ClearCommand.CanExecute(null))
        {
            _recordingAnnotationViewModel.ClearCommand.Execute(null);
        }

        disarmAnnotationInput();
    }

    public void HandleClearRequested(Action cancelInteraction)
    {
        cancelInteraction();
        _recordingAnnotationCanvas.Children.Clear();
        SyncAnnotationState();
    }

    public void ApplyUndo(IEnumerable<object> elements)
    {
        foreach (var element in elements.OfType<UIElement>())
        {
            _recordingAnnotationCanvas.Children.Remove(element);
        }

        SyncAnnotationState();
    }

    public void ApplyRedo(IEnumerable<object> elements)
    {
        foreach (var element in elements.OfType<UIElement>())
        {
            _recordingAnnotationCanvas.Children.Add(element);
        }

        SyncAnnotationState();
    }

    public void UpdateCursor(Cursor cursor)
    {
        _recordingAnnotationCanvas.Cursor = cursor;
    }

    public bool HasActiveEditor()
    {
        return _recordingAnnotationCanvas.Children.OfType<TextBox>().Any();
    }

    public void SyncAnnotationState()
    {
        var numberCount = _recordingAnnotationCanvas.Children
            .OfType<FrameworkElement>()
            .Count(element => element.Tag is "number");
        _recordingAnnotationViewModel.SyncAnnotationState(_recordingAnnotationCanvas.Children.Count > 0, numberCount);
    }
}
