using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public class ScreenRecordingServiceTests
{
    private static ScreenRecordingService CreateSut() =>
        new(NullLogger<ScreenRecordingService>.Instance, Options.Create(new RecordingOptions()));

    [Fact]
    public void IsRecording_IsFalse_BeforeStart()
    {
        using var svc = CreateSut();
        Assert.False(svc.IsRecording);
    }

    [Fact]
    public void Stop_WhenNotRecording_DoesNotThrow()
    {
        using var svc = CreateSut();
        var ex = Record.Exception(() => svc.Stop());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenNotRecording_DoesNotThrow()
    {
        var svc = CreateSut();
        var ex = Record.Exception(() => svc.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Start_WithOddDimensions_TruncatesToEven()
    {
        var svc = CreateSut();
        // Width/height of 1 → truncates to 0 → Start returns early → IsRecording stays false
        svc.Start(0, 0, 1, 1, System.IO.Path.GetTempFileName());
        Assert.False(svc.IsRecording);
        svc.Dispose();
    }
}
