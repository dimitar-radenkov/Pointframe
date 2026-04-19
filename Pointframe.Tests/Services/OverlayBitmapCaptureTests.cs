using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moq;
using Pointframe.Services;
using Pointframe.Tests.Services.Handlers;
using Xunit;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Pointframe.Tests.Services;

public sealed class OverlayBitmapCaptureTests
{
    [Fact]
    public void ComposeBitmap_RestoresVisibilityAndCompositesAnnotations()
    {
        StaTestHelper.Run(() =>
        {
            var overlayWindow = new Window
            {
                Left = 100,
                Top = 50,
                Visibility = Visibility.Visible
            };
            var annotationCanvas = CreateArrangedCanvas(40, 20);
            var annotation = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Red
            };
            Canvas.SetLeft(annotation, 5);
            Canvas.SetTop(annotation, 5);
            annotationCanvas.Children.Add(annotation);
            annotationCanvas.UpdateLayout();

            var captureMock = new Mock<IScreenCaptureService>();
            captureMock
                .Setup(service => service.Capture(220, 110, 80, 40))
                .Callback(() => Assert.Equal(Visibility.Hidden, overlayWindow.Visibility))
                .Returns(CreateSolidBitmap(80, 40, Colors.Blue));

            var sut = new OverlayBitmapCapture(
                overlayWindow,
                annotationCanvas,
                captureMock.Object,
                () => new Rect(10, 5, 40, 20),
                () => new Int32Rect(220, 110, 80, 40),
                () => 2.0,
                () => 2.0);

            var result = sut.ComposeBitmap();

            captureMock.VerifyAll();
            Assert.Equal(Visibility.Visible, overlayWindow.Visibility);
            Assert.True(result.IsFrozen);

            var pixel = ReadPixel(result, 12, 12);
            Assert.True(pixel.R > 200);
            Assert.True(pixel.G < 50);
            Assert.True(pixel.B < 50);
        });
    }

    [Fact]
    public void ComposeBitmap_WhenRestoreVisibilityDisabled_LeavesOverlayHidden()
    {
        StaTestHelper.Run(() =>
        {
            var overlayWindow = new Window
            {
                Left = 0,
                Top = 0,
                Visibility = Visibility.Visible
            };
            var annotationCanvas = CreateArrangedCanvas(10, 10);
            var captureMock = new Mock<IScreenCaptureService>();
            captureMock.Setup(service => service.Capture(0, 0, 10, 10)).Returns(CreateSolidBitmap(10, 10, Colors.Blue));

            var sut = new OverlayBitmapCapture(
                overlayWindow,
                annotationCanvas,
                captureMock.Object,
                () => new Rect(0, 0, 10, 10),
                () => new Int32Rect(0, 0, 10, 10),
                () => 1.0,
                () => 1.0);

            _ = sut.ComposeBitmap(restoreOverlayVisibilityAfterCapture: false);

            Assert.Equal(Visibility.Hidden, overlayWindow.Visibility);
        });
    }

    private static Canvas CreateArrangedCanvas(double width, double height)
    {
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.Transparent
        };
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        return canvas;
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

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var formattedBitmap = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytesPerPixel = (formattedBitmap.Format.BitsPerPixel + 7) / 8;
        var pixel = new byte[bytesPerPixel];
        formattedBitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, bytesPerPixel, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}