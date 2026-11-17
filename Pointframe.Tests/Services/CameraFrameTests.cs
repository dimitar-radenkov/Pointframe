using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CameraFrameTests
{
    [Fact]
    public void Rent_AllocatesAnExactStrideAndLength()
    {
        using var frame = CameraFrame.Rent(10, 6);

        Assert.Equal(40, frame.Stride);
        Assert.Equal(240, frame.Length);
        Assert.True(frame.Pixels.Length >= 240);
    }

    [Fact]
    public void Dispose_IsIdempotentAndBlocksFurtherAccess()
    {
        var frame = CameraFrame.Rent(2, 2);

        frame.Dispose();
        frame.Dispose();

        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Theory]
    [InlineData(640, 1)]
    [InlineData(1280, 1)]
    [InlineData(1281, 3)]
    [InlineData(1920, 3)]
    [InlineData(3840, 6)]
    public void GetDecimationStep_OnlyShrinksVeryLargeModes(int width, int expected)
    {
        Assert.Equal(expected, CameraFrameConverter.GetDecimationStep(width));
    }

    [Fact]
    public void CopyDecimated_WithStepOne_CopiesThePixelsUnchanged()
    {
        var source = new byte[4 * 2 * 4];
        for (var index = 0; index < source.Length; index++)
        {
            source[index] = (byte)index;
        }

        using var frame = CameraFrameConverter.CopyDecimated(source, 4, 2, 16, 1);

        Assert.Equal(4, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(source, frame.Pixels.AsSpan(0, frame.Length).ToArray());
    }

    [Fact]
    public void CopyDecimated_WithStepTwo_KeepsEverySecondPixelAndRow()
    {
        // 4x4 image where each pixel's B channel is its index.
        var source = new byte[4 * 4 * 4];
        for (var pixel = 0; pixel < 16; pixel++)
        {
            source[pixel * 4] = (byte)pixel;
        }

        using var frame = CameraFrameConverter.CopyDecimated(source, 4, 4, 16, 2);

        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(0, frame.Pixels[0]);
        Assert.Equal(2, frame.Pixels[4]);
        Assert.Equal(8, frame.Pixels[8]);
        Assert.Equal(10, frame.Pixels[12]);
    }

    [Theory]
    [InlineData(CameraFailureReasons.NoDevice)]
    [InlineData(CameraFailureReasons.Busy)]
    [InlineData(CameraFailureReasons.Stalled)]
    public void Normalize_KeepsKnownReasons(string reason)
    {
        Assert.Equal(reason, CameraFailureReasons.Normalize(reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Logitech C920")]
    public void Normalize_MapsEverythingElseToOther(string? reason)
    {
        Assert.Equal(CameraFailureReasons.Other, CameraFailureReasons.Normalize(reason));
    }

    [Fact]
    public void FromException_MapsAccessDeniedAndBusy()
    {
        Assert.Equal(CameraFailureReasons.AccessDenied, CameraFailureReasons.FromException(new UnauthorizedAccessException()));
        Assert.Equal(CameraFailureReasons.Busy, CameraFailureReasons.FromException(new System.Runtime.InteropServices.COMException("busy", unchecked((int)0xC00D3704))));
        Assert.Equal(CameraFailureReasons.Other, CameraFailureReasons.FromException(new InvalidOperationException()));
    }
}
