using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SmartRedactionServiceTests
{
    private static readonly SensitiveDataType[] AllBuiltInPatternTypes =
    [
        SensitiveDataType.Email,
        SensitiveDataType.Phone,
        SensitiveDataType.UrlQueryToken,
        SensitiveDataType.Ipv4,
        SensitiveDataType.AccessKeyLike,
        SensitiveDataType.JwtLike,
    ];

    public static TheoryData<SensitiveDataType, string> BuiltInPatternCases => new()
    {
        { SensitiveDataType.Email, "dev@example.com" },
        { SensitiveDataType.Phone, "555-123-4567" },
        { SensitiveDataType.UrlQueryToken, "token=abc123def456" },
        { SensitiveDataType.Ipv4, "192.168.10.42" },
        { SensitiveDataType.AccessKeyLike, "ghp_abcdefghijklmnopqrstuvwxyz123456" },
        { SensitiveDataType.JwtLike, "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4iLCJpYXQiOjE1MTYyMzkwMjJ9.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c" },
    };

    [Theory]
    [MemberData(nameof(BuiltInPatternCases))]
    public async Task DetectAsync_WhenBuiltInPatternPresent_ReturnsMatchingSuggestionType(
        SensitiveDataType expectedType,
        string sample)
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(sample, new OcrTextWord(sample, new Int32Rect(10, 10, 260, 20))),
            ]);

        var settingsService = CreateSettingsService(new UserSettings
        {
            SmartRedactionExcludedBuiltInTypes =
            [
                .. AllBuiltInPatternTypes.Where(type => type != expectedType),
            ],
        });
        var sut = new SmartRedactionService(ocr.Object, settingsService.Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(600, 200));

        Assert.Contains(suggestions, suggestion => suggestion.Type == expectedType);
    }

    [Fact]
    public async Task DetectAsync_WhenSensitiveTextPresent_ReturnsExpectedSuggestions()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Contact dev@example.com",
                    new OcrTextWord("Contact", new Int32Rect(10, 10, 60, 20)),
                    new OcrTextWord("dev@example.com", new Int32Rect(80, 10, 180, 20))),
                CreateLine(
                    "token=abc123def456",
                    new OcrTextWord("token=abc123def456", new Int32Rect(20, 50, 220, 24))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(400, 200));

        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Email);
        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.UrlQueryToken);
    }

    [Fact]
    public async Task DetectAsync_WhenPatternsOverlap_DeduplicatesByPixelBounds()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "token=ghp_abcdefghijklmnopqrstuvwxyz123456",
                    new OcrTextWord(
                        "token=ghp_abcdefghijklmnopqrstuvwxyz123456",
                        new Int32Rect(25, 40, 280, 24))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(500, 240));

        Assert.Single(suggestions);
    }

    [Fact]
    public async Task DetectAsync_WhenNoMatches_ReturnsEmpty()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Release notes v1.2.3",
                    new OcrTextWord("Release", new Int32Rect(10, 10, 60, 20)),
                    new OcrTextWord("notes", new Int32Rect(80, 10, 50, 20)),
                    new OcrTextWord("v1.2.3", new Int32Rect(140, 10, 60, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(300, 120));

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task DetectAsync_WhenBuiltInTypeExcluded_SkipsThatBuiltInPattern()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Contact dev@example.com",
                    new OcrTextWord("Contact", new Int32Rect(10, 10, 60, 20)),
                    new OcrTextWord("dev@example.com", new Int32Rect(80, 10, 180, 20))),
            ]);

        var settingsService = CreateSettingsService(new UserSettings
        {
            SmartRedactionExcludedBuiltInTypes =
            [
                SensitiveDataType.Email,
            ],
        });
        var sut = new SmartRedactionService(ocr.Object, settingsService.Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(300, 120));

        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.Email);
    }

    [Fact]
    public async Task DetectAsync_WhenIpv4Present_DetectsIpv4InsteadOfPhone()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "IPv4: 192.168.10.42",
                    new OcrTextWord("IPv4:", new Int32Rect(10, 10, 48, 20)),
                    new OcrTextWord("192.168.10.42", new Int32Rect(64, 10, 140, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(300, 120));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Ipv4);
        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
    }

    [Fact]
    public async Task DetectAsync_WhenPhoneHasOcrDigitSubstitutions_StillDetectsPhone()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Phone: SSS-123-4±8'",
                    new OcrTextWord("Phone:", new Int32Rect(10, 10, 45, 20)),
                    new OcrTextWord("SSS-123-4±8'", new Int32Rect(62, 10, 140, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(300, 120));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
    }

    [Fact]
    public async Task DetectAsync_WhenIpv4HasOcrDigitSubstitutions_StillDetectsIpv4()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "IPv4: 192.16B.10.42",
                    new OcrTextWord("IPv4:", new Int32Rect(10, 10, 48, 20)),
                    new OcrTextWord("192.16B.10.42", new Int32Rect(64, 10, 140, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(320, 120));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Ipv4);
        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
    }

    [Fact]
    public async Task DetectAsync_WhenIpv4HasOcrSpacingArtifacts_DetectsIpv4InsteadOfPhone()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "192 . 168 . 10 . 42",
                    new OcrTextWord("192", new Int32Rect(10, 10, 32, 20)),
                    new OcrTextWord(".", new Int32Rect(44, 10, 8, 20)),
                    new OcrTextWord("168", new Int32Rect(54, 10, 32, 20)),
                    new OcrTextWord(".", new Int32Rect(88, 10, 8, 20)),
                    new OcrTextWord("10", new Int32Rect(98, 10, 20, 20)),
                    new OcrTextWord(".", new Int32Rect(120, 10, 8, 20)),
                    new OcrTextWord("42", new Int32Rect(130, 10, 20, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(300, 120));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.Ipv4);
        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
    }

    [Fact]
    public async Task DetectAsync_WhenIpv4TypeExcluded_DoesNotFallbackToPhone()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "192 . 168 . 10 . 42",
                    new OcrTextWord("192", new Int32Rect(10, 10, 32, 20)),
                    new OcrTextWord(".", new Int32Rect(44, 10, 8, 20)),
                    new OcrTextWord("168", new Int32Rect(54, 10, 32, 20)),
                    new OcrTextWord(".", new Int32Rect(88, 10, 8, 20)),
                    new OcrTextWord("10", new Int32Rect(98, 10, 20, 20)),
                    new OcrTextWord(".", new Int32Rect(120, 10, 8, 20)),
                    new OcrTextWord("42", new Int32Rect(130, 10, 20, 20))),
            ]);

        var settingsService = CreateSettingsService(new UserSettings
        {
            SmartRedactionExcludedBuiltInTypes =
            [
                SensitiveDataType.Ipv4,
            ],
        });
        var sut = new SmartRedactionService(ocr.Object, settingsService.Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(300, 120));

        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.Phone);
        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.Ipv4);
    }

    [Fact]
    public async Task DetectAsync_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .Returns<BitmapSource, CancellationToken>((_, cancellationToken) =>
                Task.FromCanceled<IReadOnlyList<OcrTextLine>>(cancellationToken));

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.DetectAsync(CreateBitmap(), cts.Token));
    }

    [Fact]
    public async Task DetectAsync_WhenEnabledCustomPatternMatches_ReturnsCustomPatternSuggestion()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Customer ID: CUST-12345",
                    new OcrTextWord("Customer", new Int32Rect(10, 10, 70, 20)),
                    new OcrTextWord("ID:", new Int32Rect(84, 10, 30, 20)),
                    new OcrTextWord("CUST-12345", new Int32Rect(120, 10, 100, 20))),
            ]);

        var settingsService = CreateSettingsService(new UserSettings
        {
            CustomRedactionPatterns =
            [
                new SmartRedactionPattern
                {
                    Name = "Customer ID",
                    Pattern = @"\bCUST-\d{5}\b",
                    IsEnabled = true,
                },
            ],
        });
        var sut = new SmartRedactionService(ocr.Object, settingsService.Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(400, 200));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.CustomPattern);
    }

    [Fact]
    public async Task DetectAsync_WhenCustomPatternInvalid_DoesNotThrowAndSkipsPattern()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Value CUST-12345",
                    new OcrTextWord("Value", new Int32Rect(10, 10, 50, 20)),
                    new OcrTextWord("CUST-12345", new Int32Rect(70, 10, 100, 20))),
            ]);

        var settingsService = CreateSettingsService(new UserSettings
        {
            CustomRedactionPatterns =
            [
                new SmartRedactionPattern
                {
                    Name = "Broken Pattern",
                    Pattern = @"(\w+",
                    IsEnabled = true,
                },
            ],
        });
        var sut = new SmartRedactionService(ocr.Object, settingsService.Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(400, 200));

        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.CustomPattern);
    }

    [Fact]
    public async Task DetectAsync_WhenCustomPatternDisabled_DoesNotMatch()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "Customer ID: CUST-12345",
                    new OcrTextWord("Customer", new Int32Rect(10, 10, 70, 20)),
                    new OcrTextWord("ID:", new Int32Rect(84, 10, 30, 20)),
                    new OcrTextWord("CUST-12345", new Int32Rect(120, 10, 100, 20))),
            ]);

        var settingsService = CreateSettingsService(new UserSettings
        {
            CustomRedactionPatterns =
            [
                new SmartRedactionPattern
                {
                    Name = "Customer ID",
                    Pattern = @"\bCUST-\d{5}\b",
                    IsEnabled = false,
                },
            ],
        });
        var sut = new SmartRedactionService(ocr.Object, settingsService.Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(400, 200));

        Assert.DoesNotContain(suggestions, suggestion => suggestion.Type == SensitiveDataType.CustomPattern);
    }

    [Fact]
    public async Task DetectAsync_WhenAccessKeyLikeTextIsSplitAcrossWords_StillDetectsMatch()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "ghp_abcdefghijklmnopqrstuvwxyz123456",
                    new OcrTextWord("ghp_abcdefghijklmnop", new Int32Rect(10, 10, 140, 20)),
                    new OcrTextWord("qrstuvwxyz123456", new Int32Rect(152, 10, 120, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(400, 120));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.AccessKeyLike);
    }

    [Fact]
    public async Task DetectAsync_WhenJwtLikeTextIsSplitAcrossWords_StillDetectsMatch()
    {
        var ocr = new Mock<IOcrRegionService>();
        ocr.Setup(service => service.RecognizeLines(It.IsAny<BitmapSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                CreateLine(
                    "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4iLCJpYXQiOjE1MTYyMzkwMjJ9.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
                    new OcrTextWord("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4iLCJpYXQiOjE1MTYyMzkwMjJ9.sflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5", new Int32Rect(10, 10, 300, 20)),
                    new OcrTextWord("c", new Int32Rect(312, 10, 8, 20))),
            ]);

        var sut = new SmartRedactionService(ocr.Object, CreateSettingsService().Object);

        var suggestions = await sut.DetectAsync(CreateBitmap(500, 140));

        Assert.Contains(suggestions, suggestion => suggestion.Type == SensitiveDataType.JwtLike);
    }

    private static OcrTextLine CreateLine(string text, params OcrTextWord[] words)
    {
        return new OcrTextLine(text, new Int32Rect(0, 0, 1, 1), words);
    }

    private static BitmapSource CreateBitmap(int width = 2, int height = 2)
    {
        var pixels = new byte[width * height * 4];
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

    private static Mock<IUserSettingsService> CreateSettingsService(UserSettings? settings = null)
    {
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(settings ?? new UserSettings());
        return settingsService;
    }
}
