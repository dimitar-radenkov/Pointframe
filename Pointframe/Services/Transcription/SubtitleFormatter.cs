using System.Globalization;
using System.Text;

namespace Pointframe.Services;

public static class SubtitleFormatter
{
    // SRT uses CRLF and a blank line to terminate each cue. Emitting bare LF, or a
    // cue whose body is empty, makes strict parsers drop or misnumber every cue
    // that follows — so blank segments are skipped rather than written out empty.
    private const string LineEnding = "\r\n";

    public static string FormatSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        var index = 0;

        foreach (var segment in segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            index++;
            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append(LineEnding);
            builder.Append(FormatTimestamp(segment.Start))
                .Append(" --> ")
                .Append(FormatTimestamp(segment.End))
                .Append(LineEnding);
            builder.Append(text).Append(LineEnding).Append(LineEnding);
        }

        return builder.ToString();
    }

    public static string FormatPlainText(IReadOnlyList<TranscriptSegment> segments)
    {
        var lines = segments
            .Select(segment => segment.Text.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static bool HasSpeech(IReadOnlyList<TranscriptSegment> segments)
    {
        return segments.Any(segment => !string.IsNullOrWhiteSpace(segment.Text));
    }

    internal static string FormatTimestamp(TimeSpan time)
    {
        var hours = (int)time.TotalHours;
        var minutes = time.Minutes;
        var seconds = time.Seconds;
        var milliseconds = time.Milliseconds;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:D2}:{minutes:D2}:{seconds:D2},{milliseconds:D3}");
    }
}
