using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class FfmpegInputBannerTests
{
    [Theory]
    [InlineData("Input #0, rawvideo, from 'pipe:0':", 0)]
    [InlineData("Input #1, dshow, from 'audio=USB Mic':", 1)]
    public void IsInputHeader_ReadsTheInputIndex(string line, int expected)
    {
        Assert.True(FfmpegInputBanner.IsInputHeader(line, out var index));
        Assert.Equal(expected, index);
    }

    [Fact]
    public void IsInputHeader_IgnoresOtherLines()
    {
        Assert.False(FfmpegInputBanner.IsInputHeader("  Duration: N/A, start: 1731772345.123456, bitrate: 1 kb/s", out _));
    }

    [Fact]
    public void TryParseStartEpoch_ReadsWallclockStart()
    {
        var start = FfmpegInputBanner.TryParseStartEpoch("  Duration: N/A, start: 1731772345.123456, bitrate: 295488 kb/s");

        Assert.Equal(1731772345.123456, start!.Value, 5);
    }

    [Theory]
    [InlineData("  Duration: N/A, start: 0.000000, bitrate: 295488 kb/s")]
    [InlineData("  Duration: N/A, bitrate: 295488 kb/s")]
    [InlineData("  Duration: N/A, start: abc, bitrate: 1 kb/s")]
    [InlineData("")]
    public void TryParseStartEpoch_ReturnsNullWhenMissingOrNotAnEpoch(string line)
    {
        Assert.Null(FfmpegInputBanner.TryParseStartEpoch(line));
    }

    [Fact]
    public void ComputeVideoStartOffset_IsTheDelayBetweenFirstCaptureAndFfmpegStart()
    {
        var captured = DateTimeOffset.FromUnixTimeMilliseconds(1_731_772_343_000);

        var offset = FfmpegInputBanner.ComputeVideoStartOffset(captured, 1_731_772_344.2);

        Assert.Equal(1.2, offset!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void ComputeVideoStartOffset_AllowsAnSlightlyNegativeOffset()
    {
        var captured = DateTimeOffset.FromUnixTimeMilliseconds(1_731_772_343_000);

        var offset = FfmpegInputBanner.ComputeVideoStartOffset(captured, 1_731_772_342.8);

        Assert.Equal(-0.2, offset!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1_731_772_300.0)]
    [InlineData(1_731_773_000.0)]
    public void ComputeVideoStartOffset_ReturnsNullWhenUnknownOrImplausible(double? ffmpegStart)
    {
        var captured = DateTimeOffset.FromUnixTimeMilliseconds(1_731_772_343_000);

        Assert.Null(FfmpegInputBanner.ComputeVideoStartOffset(captured, ffmpegStart));
    }
}
