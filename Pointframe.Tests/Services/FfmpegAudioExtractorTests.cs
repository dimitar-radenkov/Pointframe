using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class FfmpegAudioExtractorTests
{
    [Fact]
    public void BuildArguments_ProducesWhisperCompatiblePcm()
    {
        var args = new List<string>();

        FfmpegAudioExtractor.BuildArguments(args, @"C:\videos\clip.mp4", @"C:\temp\out.wav");

        // Whisper requires 16 kHz mono signed 16-bit PCM; -vn drops the video stream.
        Assert.Equal(
            new[]
            {
                "-y",
                "-i", @"C:\videos\clip.mp4",
                "-vn",
                "-ac", "1",
                "-ar", "16000",
                "-c:a", "pcm_s16le",
                @"C:\temp\out.wav",
            },
            args);
    }

    [Fact]
    public void BuildArguments_PassesPathsUnquoted()
    {
        var args = new List<string>();

        // ArgumentList escapes for us, so a path with spaces must arrive verbatim —
        // pre-quoting here would produce a literal quote in the filename.
        FfmpegAudioExtractor.BuildArguments(args, @"C:\my videos\a clip.mp4", @"C:\temp dir\out.wav");

        Assert.Contains(@"C:\my videos\a clip.mp4", args);
        Assert.Contains(@"C:\temp dir\out.wav", args);
        Assert.DoesNotContain(args, argument => argument.Contains('\"'));
    }
}
