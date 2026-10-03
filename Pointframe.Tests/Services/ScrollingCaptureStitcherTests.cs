using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScrollingCaptureStitcherTests
{
    private const int Width = 60;

    [Fact]
    public void Append_UnevenScrollSteps_RebuildsThePageExactly()
    {
        var page = CreatePage(Width, 600, seed: 1);
        int[] offsets = [0, 37, 95, 140, 233, 301, 390, 452, 480];
        var stitcher = new ScrollingCaptureStitcher(Crop(page, offsets[0], 120));

        for (var i = 1; i < offsets.Length; i++)
        {
            var step = stitcher.Append(Crop(page, offsets[i], 120));

            Assert.Equal(ScrollStepOutcome.Appended, step.Outcome);
            Assert.Equal(offsets[i] - offsets[i - 1], step.Shift);
        }

        AssertSamePixels(page, stitcher.Build());
    }

    [Fact]
    public void Append_StickyHeaderAndFooter_KeepsEachOnceWithNoDuplicatedOrMissingRows()
    {
        var header = CreatePage(Width, 12, seed: 2);
        var footer = CreatePage(Width, 9, seed: 3);
        var page = CreatePage(Width, 500, seed: 4);
        int[] offsets = [0, 60, 125, 190, 260, 330, 400];
        var stitcher = new ScrollingCaptureStitcher(Stack(header, Crop(page, offsets[0], 100), footer));

        for (var i = 1; i < offsets.Length; i++)
        {
            Assert.Equal(ScrollStepOutcome.Appended, stitcher.Append(Stack(header, Crop(page, offsets[i], 100), footer)).Outcome);
        }

        AssertSamePixels(Stack(header, page, footer), stitcher.Build());
    }

    [Fact]
    public void Append_ToleratesAMovingScrollbarThumbAndABlinkingCaret()
    {
        // Real proportions: a scrollbar is about 2-3% of a content region's width.
        const int width = 200;
        const int thumbColumns = 6;
        var page = CreatePage(width, 400, seed: 5);
        int[] offsets = [0, 50, 110, 170, 230, 280];
        var stitcher = new ScrollingCaptureStitcher(WithThumbAndCaret(Crop(page, 0, 120), offsets[0], caretVisible: true));

        for (var i = 1; i < offsets.Length; i++)
        {
            var frame = WithThumbAndCaret(Crop(page, offsets[i], 120), offsets[i], caretVisible: i % 2 == 0);
            var step = stitcher.Append(frame);

            Assert.Equal(ScrollStepOutcome.Appended, step.Outcome);
            Assert.Equal(offsets[i] - offsets[i - 1], step.Shift);
        }

        // The first frame showed the caret, so the image keeps it there; everything else is page content.
        var result = stitcher.Build();
        Assert.Equal(page.Height, result.Height);
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < width - thumbColumns; x++)
            {
                var isFirstFrameCaret = y is >= 40 and < 52 && x is 10 or 11;
                if (!isFirstFrameCaret)
                {
                    Assert.True(page.Row(y)[x] == result.Row(y)[x], $"Pixel ({x},{y}) differs outside the scrollbar and caret.");
                }
            }
        }
    }

    [Fact]
    public void Append_IdenticalFrame_ReportsNoMovementAndKeepsTheFirstFrame()
    {
        var frame = CreatePage(Width, 100, seed: 6);
        var stitcher = new ScrollingCaptureStitcher(frame);

        var step = stitcher.Append(Crop(frame, 0, 100));

        Assert.Equal(ScrollStepOutcome.NoMovement, step.Outcome);
        AssertSamePixels(frame, stitcher.Build());
    }

    [Fact]
    public void Append_UnrelatedFrame_ReportsNoOverlapAndKeepsWhatWasStitched()
    {
        var page = CreatePage(Width, 300, seed: 7);
        var stitcher = new ScrollingCaptureStitcher(Crop(page, 0, 100));
        Assert.Equal(ScrollStepOutcome.Appended, stitcher.Append(Crop(page, 40, 100)).Outcome);

        var step = stitcher.Append(CreatePage(Width, 100, seed: 8));

        Assert.Equal(ScrollStepOutcome.NoOverlap, step.Outcome);
        AssertSamePixels(Crop(page, 0, 140), stitcher.Build());
    }

    [Fact]
    public void Append_ScrollLargerThanTheViewport_ReportsNoOverlap()
    {
        var page = CreatePage(Width, 400, seed: 9);
        var stitcher = new ScrollingCaptureStitcher(Crop(page, 0, 100));

        var step = stitcher.Append(Crop(page, 150, 100));

        Assert.Equal(ScrollStepOutcome.NoOverlap, step.Outcome);
        Assert.Equal(100, stitcher.Height);
    }

    [Fact]
    public void Append_DifferentFrameSize_ReportsNoOverlap()
    {
        var stitcher = new ScrollingCaptureStitcher(CreatePage(Width, 100, seed: 10));

        var step = stitcher.Append(CreatePage(Width, 90, seed: 10));

        Assert.Equal(ScrollStepOutcome.NoOverlap, step.Outcome);
    }

    [Fact]
    public void PixelFrame_BitmapRoundTrip_PreservesPixels()
    {
        var frame = CreatePage(Width, 40, seed: 11);

        var roundTrip = PixelFrame.FromBitmap(frame.ToBitmap());

        AssertSamePixels(frame, roundTrip);
    }

    internal static PixelFrame CreatePage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new int[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = unchecked((int)0xFF000000) | random.Next(0x1000000);
        }

        return new PixelFrame(width, height, pixels);
    }

    internal static PixelFrame Crop(PixelFrame page, int top, int height)
    {
        return new PixelFrame(page.Width, height, page.Pixels.AsSpan(top * page.Width, height * page.Width).ToArray());
    }

    internal static void AssertSamePixels(PixelFrame expected, PixelFrame actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"Row {y} differs.");
        }
    }

    private static PixelFrame Stack(params PixelFrame[] parts)
    {
        var pixels = parts.SelectMany(part => part.Pixels).ToArray();
        return new PixelFrame(parts[0].Width, parts.Sum(part => part.Height), pixels);
    }

    private static PixelFrame WithThumbAndCaret(PixelFrame frame, int offset, bool caretVisible)
    {
        const int track = unchecked((int)0xFFE0E0E0);
        const int thumb = unchecked((int)0xFF808080);
        var pixels = (int[])frame.Pixels.Clone();
        const int thumbColumns = 6;
        var thumbTop = offset / 4;
        for (var y = 0; y < frame.Height; y++)
        {
            var color = y >= thumbTop && y < thumbTop + 30 ? thumb : track;
            for (var x = frame.Width - thumbColumns; x < frame.Width; x++)
            {
                pixels[(y * frame.Width) + x] = color;
            }
        }

        if (caretVisible)
        {
            for (var y = 40; y < 52; y++)
            {
                pixels[(y * frame.Width) + 10] = unchecked((int)0xFF000000);
                pixels[(y * frame.Width) + 11] = unchecked((int)0xFF000000);
            }
        }

        return new PixelFrame(frame.Width, frame.Height, pixels);
    }
}
