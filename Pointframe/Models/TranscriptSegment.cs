namespace Pointframe.Models;

public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);
