using System.Windows.Controls;
using SnippingTool.ViewModels;

namespace SnippingTool.Services;

internal sealed class AnnotationCanvasInteractionController
{
    private readonly Canvas _canvas;
    private readonly AnnotationViewModel _viewModel;
    private readonly AnnotationCanvasRenderer _renderer;
    private readonly Action _onAnnotationCommitted;

    public AnnotationCanvasInteractionController(
        Canvas canvas,
        AnnotationViewModel viewModel,
        AnnotationCanvasRenderer renderer,
        Action? onAnnotationCommitted = null)
    {
        _canvas = canvas;
        _viewModel = viewModel;
        _renderer = renderer;
        _onAnnotationCommitted = onAnnotationCommitted ?? (() => { });
    }

    public void HandlePointerDown(Point point)
    {
        _viewModel.BeginGroup();
        if (_viewModel.SelectedTool is AnnotationTool.Text or AnnotationTool.Number)
        {
            _renderer.BeginShape(point);
            _renderer.CommitShape(point);
            _viewModel.CommitGroup();
            _onAnnotationCommitted();
            return;
        }

        _viewModel.BeginDrawing(point);
        _canvas.CaptureMouse();
        _renderer.BeginShape(point);
    }

    public void HandlePointerMove(Point point)
    {
        if (!_viewModel.IsDragging)
        {
            return;
        }

        _viewModel.UpdateDrawing(point);
        _renderer.UpdateShape(point);
    }

    public void HandlePointerUp(Point point)
    {
        if (!_viewModel.IsDragging)
        {
            return;
        }

        _viewModel.UpdateDrawing(point);
        _canvas.ReleaseMouseCapture();
        _renderer.CommitShape(point);
        _viewModel.CommitDrawing();
        _viewModel.CommitGroup();
        _onAnnotationCommitted();
    }

    public void Cancel()
    {
        if (_viewModel.IsDragging)
        {
            _renderer.CancelShape();
            _viewModel.CancelDrawing();
        }

        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }
}
