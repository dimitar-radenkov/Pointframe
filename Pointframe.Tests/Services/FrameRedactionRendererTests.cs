using System.Windows;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class FrameRedactionRendererTests
{
    [Fact]
    public void Render_ClipsRegionAndPreservesPixelsOutsideTheRegion()
    {
        var frame = CreateFrame(5, 3);
        var original = (byte[])frame.Clone();

        new FrameRedactionRenderer().Render(
            frame,
            5,
            3,
            [new RecordingRedactionRegion(new Int32Rect(-2, 1, 4, 3), 1, RecordingRedactionMode.Pixelate)]);

        Assert.Equal(GetPixel(original, 5, 0, 0), GetPixel(frame, 5, 0, 0));
        Assert.Equal(GetPixel(original, 5, 4, 2), GetPixel(frame, 5, 4, 2));
        Assert.Equal(GetPixel(frame, 5, 0, 1), GetPixel(frame, 5, 1, 2));
    }

    [Fact]
    public void Render_PixelatesEachBlockUsingItsTopLeftPixel()
    {
        var frame = CreateFrame(16, 2);
        var firstBlockColor = GetPixel(frame, 16, 0, 0);
        var secondBlockColor = GetPixel(frame, 16, 8, 0);

        new FrameRedactionRenderer().Render(
            frame,
            16,
            2,
            [new RecordingRedactionRegion(new Int32Rect(0, 0, 16, 2), 1, RecordingRedactionMode.Pixelate)]);

        Assert.Equal(firstBlockColor, GetPixel(frame, 16, 7, 1));
        Assert.Equal(secondBlockColor, GetPixel(frame, 16, 8, 1));
    }

    [Fact]
    public void Render_OverlappingRegionsCoverTheirCombinedArea()
    {
        var frame = CreateFrame(16, 2);
        var original = (byte[])frame.Clone();

        new FrameRedactionRenderer().Render(
            frame,
            16,
            2,
            [
                new RecordingRedactionRegion(new Int32Rect(0, 0, 10, 2), 1, RecordingRedactionMode.Pixelate),
                new RecordingRedactionRegion(new Int32Rect(6, 0, 10, 2), 2, RecordingRedactionMode.Pixelate),
            ]);

        Assert.NotEqual(GetPixel(original, 16, 5, 1), GetPixel(frame, 16, 5, 1));
        Assert.NotEqual(GetPixel(original, 16, 12, 1), GetPixel(frame, 16, 12, 1));
        Assert.Equal(GetPixel(original, 16, 0, 0), GetPixel(frame, 16, 0, 0));
    }

    private static byte[] CreateFrame(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (var index = 0; index < frame.Length; index += 4)
        {
            frame[index] = (byte)(index / 4);
            frame[index + 1] = (byte)(index / 8);
            frame[index + 2] = (byte)(255 - (index / 4));
            frame[index + 3] = 255;
        }

        return frame;
    }

    private static byte[] GetPixel(byte[] frame, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return frame[offset..(offset + 4)];
    }
}