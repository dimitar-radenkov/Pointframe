namespace Pointframe.Services;

internal enum ScrollStepOutcome
{
    Appended,
    NoMovement,
    NoOverlap,
}

internal readonly record struct ScrollStepResult(ScrollStepOutcome Outcome, int Shift);

// Joins viewport frames captured while the content scrolls down into one tall image.
// Rows that stay put between frames at the top and bottom (sticky headers and footers) are fixed bands:
// the header comes from the first frame, the footer from the last, and only the band between them
// (the body) is stitched. Each new frame's shift is found by voting on rows whose content is unique,
// then confirmed with a tolerant row comparison, so a blinking caret or a moving scrollbar thumb does not
// break the match.
internal sealed class ScrollingCaptureStitcher
{
    private const double RowMismatchTolerance = 0.05;
    private const double OverlapMatchRatio = 0.9;
    private const int MinimumOverlapRows = 4;
    private const int CandidateLimit = 3;

    private readonly PixelFrame _first;
    private readonly List<int[]> _appendedRows = [];
    private PixelFrame _last;
    private bool _bandsKnown;
    private int _headerRows;
    private int _footerRows;

    public ScrollingCaptureStitcher(PixelFrame first)
    {
        ArgumentNullException.ThrowIfNull(first);
        _first = first;
        _last = first;
    }

    public int Width => _first.Width;

    public int Height => _first.Height + _appendedRows.Count;

    public int BodyHeight => _first.Height - _headerRows - _footerRows;

    public ScrollStepResult Append(PixelFrame next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Width != _first.Width || next.Height != _first.Height)
        {
            return new ScrollStepResult(ScrollStepOutcome.NoOverlap, 0);
        }

        // Exact rows here: a sparse text row shifted a few pixels can pass the tolerant comparison.
        if (CountIdenticalRows(_last, next) >= next.Height * OverlapMatchRatio)
        {
            return new ScrollStepResult(ScrollStepOutcome.NoMovement, 0);
        }

        var (header, footer) = _bandsKnown ? (_headerRows, _footerRows) : DetectFixedBands(_last, next);
        var shift = FindShift(_last, next, header, footer);
        if (shift == 0 && !_bandsKnown && (header > 0 || footer > 0))
        {
            // Rows that merely look alike (blank margins) can pose as fixed bands; retry without them.
            (header, footer) = (0, 0);
            shift = FindShift(_last, next, header, footer);
        }

        if (shift == 0)
        {
            return new ScrollStepResult(ScrollStepOutcome.NoOverlap, 0);
        }

        if (!_bandsKnown)
        {
            _headerRows = header;
            _footerRows = footer;
            _bandsKnown = true;
        }

        var bodyEnd = next.Height - _footerRows;
        for (var y = bodyEnd - shift; y < bodyEnd; y++)
        {
            _appendedRows.Add(next.Row(y).ToArray());
        }

        _last = next;
        return new ScrollStepResult(ScrollStepOutcome.Appended, shift);
    }

    public PixelFrame Build()
    {
        var width = _first.Width;
        var pixels = new int[width * Height];
        var leadingRows = _first.Height - _footerRows;
        _first.Pixels.AsSpan(0, leadingRows * width).CopyTo(pixels);

        var offset = leadingRows * width;
        foreach (var row in _appendedRows)
        {
            row.CopyTo(pixels, offset);
            offset += width;
        }

        _last.Pixels.AsSpan((_last.Height - _footerRows) * width, _footerRows * width).CopyTo(pixels.AsSpan(offset));
        return new PixelFrame(width, Height, pixels);
    }

    private static (int Header, int Footer) DetectFixedBands(PixelFrame previous, PixelFrame next)
    {
        var height = next.Height;
        var header = 0;
        while (header < height && RowsMatch(previous.Row(header), next.Row(header)))
        {
            header++;
        }

        var footer = 0;
        while (footer < height - header && RowsMatch(previous.Row(height - 1 - footer), next.Row(height - 1 - footer)))
        {
            footer++;
        }

        if (height - header - footer < MinimumOverlapRows * 2)
        {
            return (0, 0);
        }

        return (header, footer);
    }

    private static int FindShift(PixelFrame previous, PixelFrame next, int header, int footer)
    {
        var bodyHeight = next.Height - header - footer;
        if (bodyHeight <= MinimumOverlapRows)
        {
            return 0;
        }

        var previousHashes = HashRows(previous, header, bodyHeight);
        var nextHashes = HashRows(next, header, bodyHeight);
        var previousIndex = IndexUniqueRows(previousHashes);
        var nextIndex = IndexUniqueRows(nextHashes);

        var votes = new Dictionary<int, int>();
        foreach (var (hash, nextRow) in nextIndex)
        {
            if (previousIndex.TryGetValue(hash, out var previousRow))
            {
                var shift = previousRow - nextRow;
                if (shift > 0 && bodyHeight - shift >= MinimumOverlapRows)
                {
                    votes[shift] = votes.GetValueOrDefault(shift) + 1;
                }
            }
        }

        var candidates = votes
            .OrderByDescending(vote => vote.Value)
            .ThenBy(vote => vote.Key)
            .Take(CandidateLimit)
            .Select(vote => vote.Key);
        foreach (var shift in candidates)
        {
            var overlap = bodyHeight - shift;
            if (CountMatchingRows(previous, next, header + shift, header, overlap) >= overlap * OverlapMatchRatio)
            {
                return shift;
            }
        }

        return 0;
    }

    private static long[] HashRows(PixelFrame frame, int start, int count)
    {
        var hashes = new long[count];
        for (var i = 0; i < count; i++)
        {
            var hash = new HashCode();
            hash.AddBytes(MemoryMarshal.AsBytes(frame.Row(start + i)));
            hashes[i] = hash.ToHashCode();
        }

        return hashes;
    }

    private static Dictionary<long, int> IndexUniqueRows(long[] hashes)
    {
        var index = new Dictionary<long, int>();
        var duplicates = new HashSet<long>();
        for (var i = 0; i < hashes.Length; i++)
        {
            if (!duplicates.Contains(hashes[i]) && !index.TryAdd(hashes[i], i))
            {
                index.Remove(hashes[i]);
                duplicates.Add(hashes[i]);
            }
        }

        return index;
    }

    private static int CountMatchingRows(PixelFrame previous, PixelFrame next, int previousStart, int nextStart, int count)
    {
        var matching = 0;
        for (var i = 0; i < count; i++)
        {
            if (RowsMatch(previous.Row(previousStart + i), next.Row(nextStart + i)))
            {
                matching++;
            }
        }

        return matching;
    }

    private static int CountIdenticalRows(PixelFrame previous, PixelFrame next)
    {
        var identical = 0;
        for (var y = 0; y < next.Height; y++)
        {
            if (previous.Row(y).SequenceEqual(next.Row(y)))
            {
                identical++;
            }
        }

        return identical;
    }

    private static bool RowsMatch(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        if (a.SequenceEqual(b))
        {
            return true;
        }

        var allowed = (int)(a.Length * RowMismatchTolerance);
        var mismatches = 0;
        for (var x = 0; x < a.Length; x++)
        {
            if (a[x] != b[x] && ++mismatches > allowed)
            {
                return false;
            }
        }

        return true;
    }
}
