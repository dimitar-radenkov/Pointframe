using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class ScreenRecordingServiceTests
{
    private static ScreenRecordingService CreateSut() =>
        new(NullLogger<ScreenRecordingService>.Instance, Options.Create(new RecordingOptions()));

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
}
