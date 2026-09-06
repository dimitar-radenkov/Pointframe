using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SubtitleFormatterTests
{
    private static TranscriptSegment Segment(int startSeconds, int endSeconds, string text) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), text);

    [Fact]
    public void FormatSrt_UsesCrLf_NotBareLf()
    {
        var srt = SubtitleFormatter.FormatSrt([Segment(0, 2, "Hello")]);

        Assert.Contains("\r\n", srt);
        Assert.DoesNotContain(srt.Replace("\r\n", string.Empty), "\n");
    }

    [Fact]
    public void FormatSrt_ProducesWellFormedCue()
    {
        var srt = SubtitleFormatter.FormatSrt([Segment(0, 2, "Hello there")]);

        Assert.Equal("1\r\n00:00:00,000 --> 00:00:02,000\r\nHello there\r\n\r\n", srt);
    }

    [Fact]
    public void FormatSrt_SkipsBlankSegments_AndNumbersContiguously()
    {
        // Whisper emits blank segments for silence. Writing them as empty-bodied cues
        // terminates the block early and misnumbers everything after it.
        var srt = SubtitleFormatter.FormatSrt(
        [
            Segment(0, 2, "First"),
            Segment(2, 4, "   "),
            Segment(4, 6, string.Empty),
            Segment(6, 8, "Second"),
        ]);

        Assert.Equal(
            "1\r\n00:00:00,000 --> 00:00:02,000\r\nFirst\r\n\r\n" +
            "2\r\n00:00:06,000 --> 00:00:08,000\r\nSecond\r\n\r\n",
            srt);
    }

    [Fact]
    public void FormatSrt_AllBlankSegments_ProducesEmptyOutput()
    {
        Assert.Equal(string.Empty, SubtitleFormatter.FormatSrt([Segment(0, 2, " "), Segment(2, 4, "")]));
    }

    [Fact]
    public void FormatSrt_EmptyList_ProducesEmptyOutput()
    {
        Assert.Equal(string.Empty, SubtitleFormatter.FormatSrt([]));
    }

    [Fact]
    public void FormatSrt_TrimsSegmentText()
    {
        var srt = SubtitleFormatter.FormatSrt([Segment(0, 1, "  padded  ")]);

        Assert.Contains("\r\npadded\r\n", srt);
    }

    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(1.5, "00:00:01,500")]
    [InlineData(61.25, "00:01:01,250")]
    [InlineData(3661.007, "01:01:01,007")]
    public void FormatTimestamp_MatchesSrtLayout(double seconds, string expected)
    {
        Assert.Equal(expected, SubtitleFormatter.FormatTimestamp(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatTimestamp_HoursDoNotWrapAtTwentyFour()
    {
        Assert.Equal("25:00:00,000", SubtitleFormatter.FormatTimestamp(TimeSpan.FromHours(25)));
    }

    [Fact]
    public void FormatPlainText_JoinsOnlyNonBlankLines()
    {
        var text = SubtitleFormatter.FormatPlainText(
        [
            Segment(0, 2, "First"),
            Segment(2, 4, "  "),
            Segment(4, 6, "Second"),
        ]);

        Assert.Equal($"First{Environment.NewLine}Second{Environment.NewLine}", text);
    }

    [Fact]
    public void FormatPlainText_AllBlank_IsEmpty()
    {
        Assert.Equal(string.Empty, SubtitleFormatter.FormatPlainText([Segment(0, 2, "   ")]));
    }

    [Theory]
    [InlineData("Hello", true)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    public void HasSpeech_IgnoresWhitespaceOnlySegments(string text, bool expected)
    {
        Assert.Equal(expected, SubtitleFormatter.HasSpeech([Segment(0, 2, text)]));
    }

    [Fact]
    public void HasSpeech_EmptyList_IsFalse()
    {
        Assert.False(SubtitleFormatter.HasSpeech([]));
    }
}
