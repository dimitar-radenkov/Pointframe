using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Pointframe.Services.Handlers;
using Pointframe.ViewModels;

namespace Pointframe.Services;

internal sealed class AnnotationCanvasRenderer
{
    private readonly Canvas _canvas;
    private readonly AnnotationViewModel _vm;
    private readonly Action<UIElement> _onAdd;
    private readonly ILogger<AnnotationCanvasRenderer> _logger;
    private readonly Dictionary<AnnotationTool, IAnnotationShapeHandler> _handlers;

    private IAnnotationShapeHandler? _activeHandler;

    private readonly Dictionary<Color, SolidColorBrush> _brushCache = [];

    private BitmapSource? _backgroundCapture;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    public BitmapSource? BackgroundCapture => _backgroundCapture;

    public void SetBackground(BitmapSource background, double dpiX, double dpiY)
    {
        _backgroundCapture = background.Format == PixelFormats.Bgra32
            ? background
            : new FormatConvertedBitmap(background, PixelFormats.Bgra32, null, 0);
        _dpiX = dpiX;
        _dpiY = dpiY;
    }

    public Color? SamplePixelColor(Point dipPoint)
    {
        if (_backgroundCapture is null)
        {
            return null;
        }

        var px = Math.Clamp((int)Math.Floor(dipPoint.X * _dpiX), 0, _backgroundCapture.PixelWidth - 1);
        var py = Math.Clamp((int)Math.Floor(dipPoint.Y * _dpiY), 0, _backgroundCapture.PixelHeight - 1);
        var bytes = new byte[4];
        _backgroundCapture.CopyPixels(new Int32Rect(px, py, 1, 1), bytes, 4, 0);
        return Color.FromRgb(bytes[2], bytes[1], bytes[0]); // BGRA → RGB
    }

    public BitmapSource? CropLoupeRegion(Point dipCenter, int halfPixels)
    {
        if (_backgroundCapture is null)
        {
            return null;
        }

        var cx = (int)Math.Floor(dipCenter.X * _dpiX);
        var cy = (int)Math.Floor(dipCenter.Y * _dpiY);
        var fullSize = halfPixels * 2 + 1;
        var x = Math.Clamp(cx - halfPixels, 0, Math.Max(0, _backgroundCapture.PixelWidth - fullSize));
        var y = Math.Clamp(cy - halfPixels, 0, Math.Max(0, _backgroundCapture.PixelHeight - fullSize));
        var w = Math.Min(fullSize, _backgroundCapture.PixelWidth);
        var h = Math.Min(fullSize, _backgroundCapture.PixelHeight);
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        var crop = new CroppedBitmap(_backgroundCapture, new Int32Rect(x, y, w, h));
        crop.Freeze();
        return crop;
    }

    public AnnotationCanvasRenderer(
        Canvas canvas,
        AnnotationViewModel vm,
        Action<UIElement> onAdd,
        ILogger<AnnotationCanvasRenderer> logger,
        Action? onCanvasChanged = null,
        Func<BlurShapeParameters, BitmapSource?>? captureLiveBlurSource = null)
    {
        _canvas = canvas;
        _vm = vm;
        _onAdd = onAdd;
        _logger = logger;

        _handlers = new Dictionary<AnnotationTool, IAnnotationShapeHandler>
        {
            [AnnotationTool.Arrow] = new ArrowShapeHandler(GetShapeParameters),
            [AnnotationTool.Rectangle] = new RectShapeHandler(GetShapeParameters),
            [AnnotationTool.Text] = new TextShapeHandler(_vm.ReplaceTrackedElement, _vm.RemoveTrackedElement, onCanvasChanged),
            [AnnotationTool.Highlight] = new HighlightShapeHandler(GetShapeParameters),
            [AnnotationTool.Pen] = new PenShapeHandler(GetShapeParameters),
            [AnnotationTool.Line] = new LineShapeHandler(GetShapeParameters),
            [AnnotationTool.Circle] = new EllipseShapeHandler(GetShapeParameters),
            [AnnotationTool.Number] = new NumberShapeHandler(_vm.IncrementNumberCounter),
            [AnnotationTool.Blur] = new BlurShapeHandler(GetShapeParameters, () => _backgroundCapture, () => _dpiX, () => _dpiY, captureLiveBlurSource),
            [AnnotationTool.Callout] = new CalloutShapeHandler(GetShapeParameters, _vm.ReplaceTrackedElement, _vm.RemoveTrackedElement, onCanvasChanged),
            [AnnotationTool.ColorPicker] = new ColorPickerShapeHandler(),
            [AnnotationTool.PixelRuler] = new PixelRulerShapeHandler(GetShapeParameters),
        };
    }

    private SolidColorBrush ActiveBrush()
    {
        var color = _vm.ActiveColor;
        if (!_brushCache.TryGetValue(color, out var brush))
        {
            brush = new SolidColorBrush(color);
            brush.Freeze();
            _brushCache[color] = brush;
        }

        return brush;
    }
    private ShapeParameters? GetShapeParameters() => _vm.TryGetShapeParameters();

    public void BeginShape(Point p)
    {
        _logger.LogDebug("Shape begin: {Tool}", _vm.SelectedTool);
        if (!_handlers.TryGetValue(_vm.SelectedTool, out var handler))
        {
            return;
        }

        _activeHandler = handler;
        handler.Begin(p, ActiveBrush(), _vm.StrokeThickness, _canvas);
    }

    public void UpdateShape(Point p)
    {
        if (_activeHandler is null)
        {
            return;
        }

        _activeHandler.Update(p);
    }

    public void CommitShape(Point p)
    {
        _logger.LogDebug("Shape committed: {Tool}", _vm.SelectedTool);
        if (_activeHandler is null)
        {
            return;
        }

        _activeHandler.Update(p);
        _activeHandler.Commit(_canvas, _onAdd);
        _activeHandler = null;
    }

    public void CancelShape()
    {
        _activeHandler?.Cancel(_canvas);
        _activeHandler = null;
    }
}
