using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services.Handlers;
using Xunit;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class BlurShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_WithBackground_TracksBlurredImage()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var background = CreateSolidBitmap(100, 80, Colors.Blue);
            ShapeParameters? current = new BlurShapeParameters(10, 15, 30, 20);
            var handler = new BlurShapeHandler(() => current, () => background, () => 1.0, () => 1.0);

            handler.Begin(new Point(10, 15), new SolidColorBrush(Colors.White), 1, canvas);

            var draft = Assert.IsType<Rectangle>(Assert.Single(canvas.Children));
            handler.Update(new Point(40, 35));

            Assert.Equal(10, Canvas.GetLeft(draft));
            Assert.Equal(15, Canvas.GetTop(draft));
            Assert.Equal(30, draft.Width);
            Assert.Equal(20, draft.Height);

            handler.Commit(canvas, tracked.Add);

            var image = Assert.IsType<Image>(Assert.Single(canvas.Children));
            var effect = Assert.IsType<BlurEffect>(image.Effect);
            Assert.Equal(15, effect.Radius);
            Assert.Equal(10, Canvas.GetLeft(image));
            Assert.Equal(15, Canvas.GetTop(image));
            Assert.Single(tracked);
            Assert.Same(image, tracked[0]);
        });
    }

    [Fact]
    public void Commit_WithoutBackground_RemovesDraftAndDoesNotTrack()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            ShapeParameters? current = new BlurShapeParameters(10, 15, 30, 20);
            var handler = new BlurShapeHandler(() => current, () => null, () => 1.0, () => 1.0);

            handler.Begin(new Point(10, 15), new SolidColorBrush(Colors.White), 1, canvas);
            handler.Commit(canvas, _ => throw new Xunit.Sdk.XunitException("Should not track without background."));

            Assert.Empty(canvas.Children);
        });
    }

    [Fact]
    public void Cancel_RemovesDraftRectangle()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var handler = new BlurShapeHandler(
                () => new BlurShapeParameters(5, 5, 10, 10),
                () => CreateSolidBitmap(20, 20, Colors.Blue),
                () => 1.0,
                () => 1.0);

            handler.Begin(new Point(5, 5), new SolidColorBrush(Colors.White), 1, canvas);
            handler.Cancel(canvas);

            Assert.Empty(canvas.Children);
        });
    }

    [Fact]
    public void Commit_WithoutStaticBackground_UsesLiveCaptureFallback()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            ShapeParameters? current = new BlurShapeParameters(10, 15, 30, 20);
            var handler = new BlurShapeHandler(
                () => current,
                () => null,
                () => 1.0,
                () => 1.0,
                parameters =>
                {
                    Assert.Equal(10, parameters.Left);
                    Assert.Equal(15, parameters.Top);
                    Assert.Equal(30, parameters.Width);
                    Assert.Equal(20, parameters.Height);
                    return CreateSolidBitmap(30, 20, Colors.Blue);
                });

            handler.Begin(new Point(10, 15), new SolidColorBrush(Colors.White), 1, canvas);
            handler.Commit(canvas, tracked.Add);

            var image = Assert.IsType<Image>(Assert.Single(canvas.Children));
            var effect = Assert.IsType<BlurEffect>(image.Effect);
            Assert.Equal(15, effect.Radius);
            Assert.Single(tracked);
            Assert.Same(image, tracked[0]);
        });
    }

    private static BitmapSource CreateSolidBitmap(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}