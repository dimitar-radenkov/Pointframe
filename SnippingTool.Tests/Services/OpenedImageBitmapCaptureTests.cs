using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Runtime.ExceptionServices;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class OpenedImageBitmapCaptureTests
{
    [Fact]
    public void ComposeBitmap_ReturnsBitmapWithSourcePixelDimensions()
    {
        RunInSta(() =>
        {
            var sourceBitmap = CreateSolidBitmap(200, 100, Colors.Blue);
            var canvas = new Canvas
            {
                Width = 100,
                Height = 50
            };
            var sut = new OpenedImageBitmapCapture(sourceBitmap, canvas);

            var result = sut.ComposeBitmap();

            Assert.Equal(200, result.PixelWidth);
            Assert.Equal(100, result.PixelHeight);
        });
    }

    [Fact]
    public void ComposeBitmap_CompositesAnnotationCanvasOntoSourceBitmap()
    {
        RunInSta(() =>
        {
            var sourceBitmap = CreateSolidBitmap(200, 100, Colors.Blue);
            var canvas = new Canvas
            {
                Width = 100,
                Height = 50
            };
            var annotation = new Rectangle
            {
                Width = 20,
                Height = 10,
                Fill = Brushes.Red
            };
            Canvas.SetLeft(annotation, 25);
            Canvas.SetTop(annotation, 10);
            canvas.Children.Add(annotation);
            var sut = new OpenedImageBitmapCapture(sourceBitmap, canvas);

            var result = sut.ComposeBitmap();
            var pixel = ReadPixel(result, 60, 30);

            Assert.Equal(255, pixel.A);
            Assert.True(pixel.R > 200);
            Assert.True(pixel.G < 50);
            Assert.True(pixel.B < 50);
        });
    }

    [Fact]
    public void ComposeBitmap_ReturnsFrozenBitmap()
    {
        RunInSta(() =>
        {
            var sourceBitmap = CreateSolidBitmap(50, 30, Colors.Green);
            var canvas = new Canvas
            {
                Width = 25,
                Height = 15
            };
            var sut = new OpenedImageBitmapCapture(sourceBitmap, canvas);

            var result = sut.ComposeBitmap();

            Assert.True(result.IsFrozen);
        });
    }

    private static void RunInSta(Action action)
    {
        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        capturedException?.Throw();
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

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var formattedBitmap = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytesPerPixel = (formattedBitmap.Format.BitsPerPixel + 7) / 8;
        var pixel = new byte[bytesPerPixel];
        formattedBitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, bytesPerPixel, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}