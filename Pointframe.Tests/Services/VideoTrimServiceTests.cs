using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

[Collection("FfmpegPathOverride")]
public sealed class VideoTrimServiceTests
{
    private static readonly object FfmpegPathLock = new();

    private static IDisposable UseFfmpegPathOverride(string? path)
    {
        Monitor.Enter(FfmpegPathLock);
        var previous = AppContext.GetData("SnippingTool.FfmpegPath");
        AppContext.SetData("SnippingTool.FfmpegPath", path);
        return new ActionDisposable(() =>
        {
            AppContext.SetData("SnippingTool.FfmpegPath", previous);
            Monitor.Exit(FfmpegPathLock);
        });
    }

    [Fact]
    public async Task Trim_ThrowsFileNotFoundException_WhenFfmpegMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.exe");
        using var overrideScope = UseFfmpegPathOverride(missingPath);

        var svc = new VideoTrimService(NullLogger<VideoTrimService>.Instance);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => svc.Trim(@"C:\input.mp4", @"C:\output.mp4", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));

        Assert.Equal(missingPath, ex.FileName);
    }

    [Fact]
    public async Task Trim_ThrowsArgumentOutOfRangeException_WhenEndNotAfterStart()
    {
        var svc = new VideoTrimService(NullLogger<VideoTrimService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => svc.Trim(@"C:\input.mp4", @"C:\output.mp4", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BuildArguments_StreamCopy_UsesCopyCodecAndTimestampFix()
    {
        var args = new List<string>();
        VideoTrimService.BuildArguments(args, "input.mp4", "output.mp4", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10), reEncode: false);

        Assert.Contains("copy", args);
        Assert.Contains("-avoid_negative_ts", args);
        Assert.Contains("make_zero", args);
        Assert.DoesNotContain("libx264", args);
        Assert.DoesNotContain("aac", args);
    }

    [Fact]
    public void BuildArguments_ReEncode_UsesVideoAndAudioCodecs()
    {
        var args = new List<string>();
        VideoTrimService.BuildArguments(args, "input.mp4", "output.mp4", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10), reEncode: true);

        Assert.Contains("libx264", args);
        Assert.Contains("aac", args);
        Assert.Contains("yuv420p", args);
        Assert.DoesNotContain("copy", args);
        Assert.DoesNotContain("-avoid_negative_ts", args);
    }

    [Fact]
    public void BuildArguments_FormatsTimesAsInvariantSecondsAndOrdersSeekBeforeInput()
    {
        var args = new List<string>();
        VideoTrimService.BuildArguments(args, "input.mp4", "output.mp4", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10.25), reEncode: false);

        var seekIndex = args.IndexOf("-ss");
        var inputIndex = args.IndexOf("input.mp4");
        var durationIndex = args.IndexOf("-t");

        Assert.True(seekIndex >= 0);
        Assert.Equal("1.5", args[seekIndex + 1]);
        Assert.Equal("8.75", args[durationIndex + 1]);
        Assert.True(seekIndex < inputIndex);
        Assert.Equal("output.mp4", args[^1]);
    }

    [Theory]
    [InlineData("  Duration: 00:00:06.01, start: 0.000000, bitrate: 260 kb/s", 6.01)]
    [InlineData("Duration: 01:02:03.50, start: 0", 3723.5)]
    public void ParseDuration_ReadsFfmpegHeaderDump(string ffmpegOutput, double expectedSeconds)
    {
        var result = VideoTrimService.ParseDuration(ffmpegOutput);

        Assert.NotNull(result);
        Assert.Equal(expectedSeconds, result.Value.TotalSeconds, precision: 2);
    }

    [Fact]
    public void ParseDuration_ReturnsNull_WhenNoDurationPresent()
    {
        Assert.Null(VideoTrimService.ParseDuration("Invalid data found when processing input"));
    }

    [Fact]
    public void GetDefaultOutputPath_AppendsTrimmedSuffix()
    {
        var result = VideoTrimService.GetDefaultOutputPath(@"C:\videos\recording.mp4");

        Assert.Equal(@"C:\videos\recording.trimmed.mp4", result);
    }

    [Fact]
    public void GetDefaultOutputPath_AvoidsExistingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"trim-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "recording.mp4");
        var firstTrimPath = Path.Combine(directory, "recording.trimmed.mp4");

        try
        {
            File.WriteAllBytes(firstTrimPath, [1]);

            var result = VideoTrimService.GetDefaultOutputPath(inputPath);

            Assert.Equal(Path.Combine(directory, "recording.trimmed-2.mp4"), result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _dispose;

        public ActionDisposable(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose() => _dispose();
    }
}
