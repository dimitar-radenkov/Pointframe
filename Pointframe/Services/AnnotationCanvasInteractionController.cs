using System.Windows.Controls;
using System.Windows.Media;
using Pointframe.ViewModels;

namespace Pointframe.Services;

internal sealed class AnnotationCanvasInteractionController
{
    private readonly Canvas _canvas;
    private readonly AnnotationViewModel _viewModel;
    private readonly AnnotationCanvasRenderer _renderer;
    private readonly Action _onAnnotationCommitted;
    private readonly Action<Color, Point>? _onColorPicked;
    private readonly Action<Point?>? _onLoupePositionChanged;

    public AnnotationCanvasInteractionController(
        Canvas canvas,
        AnnotationViewModel viewModel,
        AnnotationCanvasRenderer renderer,
        Action? onAnnotationCommitted = null,
        Action<Color, Point>? onColorPicked = null,
        Action<Point?>? onLoupePositionChanged = null)
    {
        _canvas = canvas;
        _viewModel = viewModel;
        _renderer = renderer;
        _onAnnotationCommitted = onAnnotationCommitted ?? (() => { });
        _onColorPicked = onColorPicked;
        _onLoupePositionChanged = onLoupePositionChanged;
    }

    public void HandlePointerDown(Point point)
    {
        if (_viewModel.SelectedTool == AnnotationTool.ColorPicker)
        {
            var color = _renderer.SamplePixelColor(point);
            if (color.HasValue)
            {
                _viewModel.ActiveColor = color.Value;
            }

            _viewModel.RevertToPreviousTool();
            _onColorPicked?.Invoke(color ?? Colors.Transparent, point);
            _onLoupePositionChanged?.Invoke(null);
            return;
        }

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
        if (_viewModel.SelectedTool == AnnotationTool.ColorPicker)
        {
            _onLoupePositionChanged?.Invoke(point);
            return;
        }

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
        _onLoupePositionChanged?.Invoke(null);

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
