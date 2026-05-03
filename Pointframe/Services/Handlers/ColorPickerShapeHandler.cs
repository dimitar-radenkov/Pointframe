using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pointframe.Services.Handlers;

// No-op handler — ColorPicker is short-circuited in AnnotationCanvasInteractionController
// before the normal Begin/Update/Commit lifecycle runs.
internal sealed class ColorPickerShapeHandler : IAnnotationShapeHandler
{
    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas) { }
    public void Update(Point point) { }
    public void Commit(Canvas canvas, Action<UIElement> trackElement) { }
    public void Cancel(Canvas canvas) { }
}
