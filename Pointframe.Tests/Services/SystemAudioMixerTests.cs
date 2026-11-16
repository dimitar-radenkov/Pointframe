using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SystemAudioMixerTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void BuildArguments_WithoutMicrophone_MapsVideoAndSystemAudioAndCopiesVideo()
    {
        var args = new List<string>();

        SystemAudioMixer.BuildArguments(args, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: false);

        Assert.Equal(["-i", "in.mp4", "-i", "sys.wav"], args.Skip(args.IndexOf("-i")).Take(4));
        Assert.DoesNotContain("-filter_complex", args);
        Assert.Contains("-shortest", args);
        AssertSequence(args, "-map", "0:v:0", "-map", "1:a:0");
        AssertSequence(args, "-c:v", "copy", "-c:a", "aac");
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void BuildArguments_WithMicrophone_MixesBothAudioTracksIntoOne()
    {
        var args = new List<string>();

        SystemAudioMixer.BuildArguments(args, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: true);

        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:a:0][1:a:0]amix=inputs=2", filter);
        AssertSequence(args, "-map", "0:v:0", "-map", "[mix]");
        Assert.Equal(1, args.Count(argument => argument == "-c:a"));
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void TryMix_WhenFfmpegFails_KeepsTheOriginalVideoUntouchedAndLeavesNoTemporaryFile()
    {
        var video = _folder.File("clip.mp4");
        var wav = _folder.File("clip.wav");
        File.WriteAllBytes(video, [1, 2, 3, 4, 5]);
        File.WriteAllBytes(wav, [9, 9, 9]);
        var mixer = new SystemAudioMixer(NullLogger<SystemAudioMixer>.Instance);

        var mixed = mixer.TryMix(video, wav, videoHasMicrophone: false);

        Assert.False(mixed);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(video));
        Assert.False(File.Exists(SystemAudioMixer.GetTemporaryPath(video)));
    }

    [Fact]
    public void GetWavPath_IsHiddenNextToTheOutput()
    {
        var path = SystemAudioMixer.GetWavPath(_folder.File("SnipRec-1.mp4"));

        Assert.Equal(_folder.File(".SnipRec-1.sysaudio.wav"), path);
    }

    private static void AssertSequence(List<string> args, params string[] expected)
    {
        for (var i = 0; i < expected.Length; i += 2)
        {
            var index = args.IndexOf(expected[i]);
            while (index >= 0 && args[index + 1] != expected[i + 1])
            {
                index = args.IndexOf(expected[i], index + 1);
            }

            Assert.True(index >= 0, $"Expected {expected[i]} {expected[i + 1]} in: {string.Join(' ', args)}");
        }
    }
}
