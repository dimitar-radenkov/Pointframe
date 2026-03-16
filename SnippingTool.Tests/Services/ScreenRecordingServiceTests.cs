using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class ScreenRecordingServiceTests
{
    private static ScreenRecordingService CreateSut() =>
        new(NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IUserSettingsService>(s => s.Current == new UserSettings()),
            new Mock<IVideoWriterFactory>().Object);

    private static ScreenRecordingService CreateSut(
        IVideoWriterFactory factory,
        UserSettings? settings = null) =>
        new(NullLogger<ScreenRecordingService>.Instance,
            Mock.Of<IUserSettingsService>(s => s.Current == (settings ?? new UserSettings())),
            factory);

    [Fact]
    public void IsRecording_IsFalse_BeforeStart()
    {
        // Arrange
        using var svc = CreateSut();

        // Assert
        Assert.False(svc.IsRecording);
    }

    [Fact]
    public void Stop_WhenNotRecording_DoesNotThrow()
    {
        // Arrange
        using var svc = CreateSut();

        // Act
        var ex = Record.Exception(() => svc.Stop());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenNotRecording_DoesNotThrow()
    {
        // Arrange
        var svc = CreateSut();

        // Act
        var ex = Record.Exception(() => svc.Dispose());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void Start_WithOddDimensions_TruncatesToEven()
    {
        // Arrange
        using var svc = CreateSut();

        // Act
        svc.Start(0, 0, 1, 1, System.IO.Path.GetTempFileName());

        // Assert
        Assert.False(svc.IsRecording);
    }

    [Theory]
    [InlineData(RecordingFormat.Mp4)]
    [InlineData(RecordingFormat.Avi)]
    public void Start_CallsFactoryWithConfiguredFormat(RecordingFormat format)
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var settings = new UserSettings { RecordingFormat = format };
        using var svc = CreateSut(mockFactory.Object, settings);

        // Act
        svc.Start(0, 0, 100, 100, System.IO.Path.GetTempFileName());

        // Assert
        mockFactory.Verify(f => f.Create(format, 100, 100, It.IsAny<int>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Start_PassesCorrectDimensionsAndFpsToFactory()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(new Mock<IVideoWriter>().Object);
        var settings = new UserSettings { RecordingFps = 30 };
        using var svc = CreateSut(mockFactory.Object, settings);

        // Act
        svc.Start(10, 20, 200, 150, "test.mp4");

        // Assert
        mockFactory.Verify(f => f.Create(It.IsAny<RecordingFormat>(), 200, 150, 30, "test.mp4"), Times.Once);
    }

    [Fact]
    public void Start_FactoryThrowsFileNotFound_PropagatesException()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Throws(new System.IO.FileNotFoundException("ffmpeg.exe not found"));
        using var svc = CreateSut(mockFactory.Object);

        // Act & Assert
        Assert.Throws<System.IO.FileNotFoundException>(() =>
            svc.Start(0, 0, 100, 100, "test.mp4"));
    }

    [Fact]
    public void Start_FactoryThrowsFileNotFound_IsRecordingRemainsFalse()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Throws(new System.IO.FileNotFoundException("ffmpeg.exe not found"));
        using var svc = CreateSut(mockFactory.Object);

        // Act
        try
        {
            svc.Start(0, 0, 100, 100, "test.mp4");
        }
        catch { }

        // Assert
        Assert.False(svc.IsRecording);
    }

    [Fact]
    public void Start_EvenDimensions_SetsIsRecordingTrue()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        // Act
        svc.Start(0, 0, 100, 100, "test.avi");

        // Assert
        Assert.True(svc.IsRecording);
    }

    [Fact]
    public void Start_TwiceConcurrently_IgnoresSecondCall()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);
        svc.Start(0, 0, 100, 100, "first.avi");

        // Act
        svc.Start(0, 0, 200, 200, "second.avi");

        // Assert — factory only called once
        mockFactory.Verify(
            f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public void Start_OddDimensions_TruncatesToEvenBeforeFactory()
    {
        // Arrange
        var mockFactory = new Mock<IVideoWriterFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<RecordingFormat>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(new Mock<IVideoWriter>().Object);
        using var svc = CreateSut(mockFactory.Object);

        // Act
        svc.Start(0, 0, 101, 151, "test.avi");

        // Assert — dimensions truncated to 100×150
        mockFactory.Verify(f => f.Create(It.IsAny<RecordingFormat>(), 100, 150, It.IsAny<int>(), It.IsAny<string>()), Times.Once);
    }
}
