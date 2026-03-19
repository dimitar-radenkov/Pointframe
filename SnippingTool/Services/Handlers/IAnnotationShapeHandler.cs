using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnippingTool.Services.Handlers;

internal interface IAnnotationShapeHandler
{
    void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas);
    void Update(Point point);
    void Commit(Canvas canvas, Action<UIElement> trackElement);
    void Cancel(Canvas canvas);
}
