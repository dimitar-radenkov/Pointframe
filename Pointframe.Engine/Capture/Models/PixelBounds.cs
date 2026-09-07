using System.Drawing;

namespace Pointframe.Engine;

public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
    public Rectangle ToRectangle()
    {
        return new Rectangle(X, Y, Width, Height);
    }
}
