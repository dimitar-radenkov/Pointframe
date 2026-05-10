using System.Windows;
namespace Pointframe;

public partial class OverlayWindow
{
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
