using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingAudioLeadTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData(100.0, 100.25, 0.25)]
    [InlineData(100.0, 105.0, 5.0)]
    public void ComputeAudioLead_InsideTheBounds_ReturnsTheDifference(double audioStart, double firstFrame, double expectedSeconds)
    {
        var lead = ScreenRecordingService.ComputeAudioLead(audioStart, firstFrame, out var reason);

        Assert.Equal(expectedSeconds, lead.TotalSeconds, 6);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(100.0, 100.0)]
    [InlineData(100.0, 99.5)]
    [InlineData(100.0, 105.5)]
    [InlineData(null, 100.0)]
    [InlineData(100.0, null)]
    public void ComputeAudioLead_OutsideTheBoundsOrUnknown_IsZeroWithAReason(double? audioStart, double? firstFrame)
    {
        var lead = ScreenRecordingService.ComputeAudioLead(audioStart, firstFrame, out var reason);

        Assert.Equal(TimeSpan.Zero, lead);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void BuildRemuxArguments_WithoutALead_IsThePlainStreamCopy()
    {
        var args = FfmpegRecordingRemuxer.BuildRemuxArguments("in.mp4", "out.mp4", null);

        Assert.Equal(new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", "in.mp4", "-c", "copy", "-movflags", "+faststart", "out.mp4" }, args);
    }

    [Fact]
    public void BuildRemuxArguments_WithALead_ReadsTheFileTwiceAndSkipsTheAudioLead()
    {
        var args = FfmpegRecordingRemuxer.BuildRemuxArguments("in.mp4", "out.mp4", TimeSpan.FromMilliseconds(250));

        Assert.Equal(
            new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", "in.mp4", "-ss", "0.25", "-i", "in.mp4", "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-shortest", "-movflags", "+faststart", "out.mp4" },
            args);
    }

    [Fact]
    public void Remux_WithALead_UsesTheTrimmedArguments()
    {
        var runs = new List<string[]>();
        var sut = CreateRemuxer(runs, failTrimmed: false);

        var duration = sut.Remux(_folder.File("in.mp4"), _folder.File("out.mp4"), TimeSpan.FromSeconds(0.4));

        Assert.NotNull(duration);
        Assert.Contains("-ss", Assert.Single(runs.Where(args => args.Contains("-c"))));
        Assert.True(File.Exists(_folder.File("out.mp4")));
    }

    [Fact]
    public void Remux_WhenTheTrimmedRemuxFails_FallsBackToThePlainRemux()
    {
        var runs = new List<string[]>();
        var sut = CreateRemuxer(runs, failTrimmed: true);

        var duration = sut.Remux(_folder.File("in.mp4"), _folder.File("out.mp4"), TimeSpan.FromSeconds(0.4));

        Assert.NotNull(duration);
        var remuxRuns = runs.Where(args => args.Contains("-c")).ToList();
        Assert.Equal(2, remuxRuns.Count);
        Assert.Contains("-ss", remuxRuns[0]);
        Assert.DoesNotContain("-ss", remuxRuns[1]);
        Assert.True(File.Exists(_folder.File("out.mp4")));
    }

    [Fact]
    public void Remux_WithoutALead_RunsOnlyThePlainRemux()
    {
        var runs = new List<string[]>();
        var sut = CreateRemuxer(runs, failTrimmed: false);

        sut.Remux(_folder.File("in.mp4"), _folder.File("out.mp4"));

        Assert.DoesNotContain(runs, args => args.Contains("-ss"));
    }

    [Fact]
    public void Recover_PassesTheAudioLeadToTheRemuxerOnlyWhenPositive()
    {
        var remuxer = new FakeRemuxer();
        var sut = new RecordingRecovery(remuxer, NullLogger<RecordingRecovery>.Instance);
        var intermediate = _folder.File(".a.recording.mp4");
        FragmentedMp4Fixture.Write(intermediate, withFragment: true);

        sut.Recover(intermediate, _folder.File("a.mp4"), TimeSpan.FromMilliseconds(300));
        var second = _folder.File(".b.recording.mp4");
        FragmentedMp4Fixture.Write(second, withFragment: true);
        sut.Recover(second, _folder.File("b.mp4"));

        Assert.Equal(new[] { TimeSpan.FromMilliseconds(300) }, remuxer.LeadCalls);
        Assert.Equal(2, remuxer.Calls.Count);
    }

    private FfmpegRecordingRemuxer CreateRemuxer(List<string[]> runs, bool failTrimmed)
    {
        File.WriteAllText(_folder.File("in.mp4"), "x");
        return new FfmpegRecordingRemuxer(
            NullLogger<FfmpegRecordingRemuxer>.Instance,
            () => "ffmpeg",
            (_, args, _) =>
            {
                runs.Add(args);
                if (args.Contains("-c"))
                {
                    if (failTrimmed && args.Contains("-ss"))
                    {
                        return (1, "boom");
                    }

                    File.WriteAllText(args[^1], "remuxed");
                    return (0, string.Empty);
                }

                return (0, "  Duration: 00:00:01.00, start: 0.000000, bitrate: 10 kb/s");
            });
    }
}
