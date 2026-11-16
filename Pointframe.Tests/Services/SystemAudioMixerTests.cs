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
        Assert.Equal("[1:a:0]apad[mix]", args[args.IndexOf("-filter_complex") + 1]);
        Assert.Contains("-shortest", args);
        AssertSequence(args, "-map", "0:v:0", "-map", "[mix]");
        AssertSequence(args, "-c:v", "copy", "-c:a", "aac");
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void BuildArguments_WithMicrophone_MixesBothAudioTracksIntoOne()
    {
        var args = new List<string>();

        SystemAudioMixer.BuildArguments(args, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: true);

        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("[0:a:0]apad[mic];[1:a:0]apad[sys];[mic][sys]amix=inputs=2", filter);
        Assert.Contains("-shortest", args);
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
    public void BuildArguments_WithMicrophoneAndPositiveOffset_TrimsTheStartOfTheSystemAudio()
    {
        var args = new List<string>();

        SystemAudioMixer.BuildArguments(args, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: true, TimeSpan.FromSeconds(1.5));

        Assert.Contains("[1:a:0]atrim=start=1.5,asetpts=PTS-STARTPTS,apad[sys]", args[args.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void BuildArguments_WithMicrophoneAndNegativeOffset_DelaysTheSystemAudio()
    {
        var args = new List<string>();

        SystemAudioMixer.BuildArguments(args, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: true, TimeSpan.FromMilliseconds(-250));

        Assert.Contains("[1:a:0]adelay=250:all=1,apad[sys]", args[args.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void BuildArguments_WithoutMicrophone_IgnoresTheOffset()
    {
        var withOffset = new List<string>();
        var without = new List<string>();

        SystemAudioMixer.BuildArguments(withOffset, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: false, TimeSpan.FromSeconds(1.2));
        SystemAudioMixer.BuildArguments(without, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: false);

        Assert.Equal(without, withOffset);
    }

    [Fact]
    public void BuildArguments_WithMicrophoneAndNoOffset_KeepsTheUnshiftedFilter()
    {
        var args = new List<string>();

        SystemAudioMixer.BuildArguments(args, "in.mp4", "sys.wav", "out.mp4", videoHasMicrophone: true, null);

        Assert.Equal("[0:a:0]apad[mic];[1:a:0]apad[sys];[mic][sys]amix=inputs=2:duration=first:dropout_transition=0[mix]", args[args.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void TryMix_WhenTheMixedFileIsShorterThanTheOriginal_KeepsTheOriginal()
    {
        var (video, wav) = Files();
        var mixer = new SystemAudioMixer(NullLogger<SystemAudioMixer>.Instance, path => path == video ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(20), WriteTemp);

        var mixed = mixer.TryMix(video, wav, videoHasMicrophone: false);

        Assert.False(mixed);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(video));
        Assert.False(File.Exists(SystemAudioMixer.GetTemporaryPath(video)));
    }

    [Fact]
    public void TryMix_WhenTheDurationCannotBeRead_KeepsTheOriginal()
    {
        var (video, wav) = Files();
        var mixer = new SystemAudioMixer(NullLogger<SystemAudioMixer>.Instance, _ => null, WriteTemp);

        Assert.False(mixer.TryMix(video, wav, videoHasMicrophone: false));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(video));
    }

    [Fact]
    public void TryMix_WhenTheLengthIsPreserved_ReplacesTheVideo()
    {
        var (video, wav) = Files();
        var mixer = new SystemAudioMixer(NullLogger<SystemAudioMixer>.Instance, _ => TimeSpan.FromSeconds(60.2), WriteTemp);

        Assert.True(mixer.TryMix(video, wav, videoHasMicrophone: false));
        Assert.Equal([7, 7], File.ReadAllBytes(video));
        Assert.False(File.Exists(SystemAudioMixer.GetTemporaryPath(video)));
    }

    [Fact]
    public void DurationsMatch_AllowsSmallDifferencesOnly()
    {
        Assert.True(SystemAudioMixer.DurationsMatch(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60.5)));
        Assert.False(SystemAudioMixer.DurationsMatch(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(58)));
        Assert.False(SystemAudioMixer.DurationsMatch(null, TimeSpan.FromSeconds(60)));
    }

    private (string Video, string Wav) Files()
    {
        var video = _folder.File("clip.mp4");
        var wav = _folder.File("clip.wav");
        File.WriteAllBytes(video, [1, 2, 3]);
        File.WriteAllBytes(wav, [9]);
        return (video, wav);
    }

    private static bool WriteTemp(string video, string wav, string temporary, bool microphone, TimeSpan? offset)
    {
        File.WriteAllBytes(temporary, [7, 7]);
        return true;
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
