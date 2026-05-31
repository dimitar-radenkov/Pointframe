using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pointframe.Services;

public sealed class BeautifierRenderService
{
    public BitmapSource Render(
        BitmapSource source,
        BeautifyBackground background,
        double padding,
        double cornerRadius,
        bool shadowEnabled,
        double shadowBlur,
        double shadowOffsetY,
        double shadowOpacity)
    {
        var paddingPx = (int)Math.Round(padding);
        var outputW = source.PixelWidth + paddingPx * 2;
        var outputH = source.PixelHeight + paddingPx * 2;

        var innerBorder = new Border
        {
            Width = source.PixelWidth,
            Height = source.PixelHeight,
            CornerRadius = new CornerRadius(cornerRadius),
            Background = new ImageBrush(source) { Stretch = Stretch.Fill },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (shadowEnabled)
        {
            innerBorder.Effect = new DropShadowEffect
            {
                BlurRadius = shadowBlur,
                ShadowDepth = shadowOffsetY,
                Direction = 270,
                Color = Colors.Black,
                Opacity = shadowOpacity,
                RenderingBias = RenderingBias.Quality,
            };
        }

        var canvas = new Border
        {
            Width = outputW,
            Height = outputH,
            Background = BeautifyPresets.GetBrush(background),
            Child = new Grid { Children = { innerBorder } },
        };

        canvas.Measure(new Size(outputW, outputH));
        canvas.Arrange(new Rect(0, 0, outputW, outputH));
        canvas.UpdateLayout();

        var rtb = new RenderTargetBitmap(outputW, outputH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(canvas);
        rtb.Freeze();
        return rtb;
    }
}
