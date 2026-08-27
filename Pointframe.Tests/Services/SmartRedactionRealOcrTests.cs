using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.Tests.Services;

public sealed class SmartRedactionRealOcrTests
{
    private readonly ITestOutputHelper _output;

    public SmartRedactionRealOcrTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectAsync_WithRealOcr_DetectsPhoneAndIpv4()
    {
        var bitmap = CreateBitmapWithText(
            "Phone: 555-123-4567",
            "IPv4: 192.168.10.42");
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(new UserSettings());
        var ocr = new WindowsOcrService();
        var lines = await ocr.RecognizeLines(bitmap);
        foreach (var line in lines)
        {
            _output.WriteLine($"LINE: {line.Text}");
            foreach (var word in line.Words)
            {
                _output.WriteLine($"  WORD: '{word.Text}' @ {word.PixelBounds}");
            }
        }

        var sut = new SmartRedactionService(ocr, settingsService.Object);
        var suggestions = await sut.DetectAsync(bitmap);
        foreach (var suggestion in suggestions)
        {
            _output.WriteLine($"SUGGESTION: {suggestion.Type} @ {suggestion.PixelBounds}");
        }

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Ipv4);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DetectAsync_WithAttachedSampleImage_DetectsPhoneAndIpv4_WhenPathIsProvided()
    {
        var samplePath = Environment.GetEnvironmentVariable("POINTFRAME_SMART_REDACTION_IMAGE_PATH");
        if (string.IsNullOrWhiteSpace(samplePath))
        {
            _output.WriteLine("POINTFRAME_SMART_REDACTION_IMAGE_PATH is not set. Skipping attached-image OCR probe.");
            return;
        }

        Assert.True(File.Exists(samplePath), $"Sample image not found at '{samplePath}'.");
        var bitmap = LoadBitmap(samplePath);
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(new UserSettings());
        var ocr = new WindowsOcrService();
        var lines = await ocr.RecognizeLines(bitmap);
        foreach (var line in lines)
        {
            _output.WriteLine($"LINE: {line.Text}");
            foreach (var word in line.Words)
            {
                _output.WriteLine($"  WORD: '{word.Text}' @ {word.PixelBounds}");
            }
        }

        var sut = new SmartRedactionService(ocr, settingsService.Object);
        var suggestions = await sut.DetectAsync(bitmap);
        foreach (var suggestion in suggestions)
        {
            _output.WriteLine($"SUGGESTION: {suggestion.Type} @ {suggestion.PixelBounds}");
        }

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Ipv4);
    }

    private static BitmapSource CreateBitmapWithText(params string[] lines)
    {
        const int width = 1600;
        const int height = 420;
        const double fontSize = 44;
        const double lineSpacing = 18;
        const double x = 28;
        var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var y = 28d;
            foreach (var line in lines)
            {
                var formattedText = new FormattedText(
                    line,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black,
                    1.0);
                context.DrawText(formattedText, new Point(x, y));
                y += formattedText.Height + lineSpacing;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
