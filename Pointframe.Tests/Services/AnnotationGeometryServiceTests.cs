using System.Windows;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class AnnotationGeometryServiceTests
{
    private readonly AnnotationGeometryService _sut = new();

    [Fact]
    public void CalculateRect_TopLeftToBottomRight_ReturnsCorrectBounds()
    {
        // Arrange
        var start = new Point(10, 20);
        var end = new Point(110, 70);

        // Act
        var (left, top, width, height) = _sut.CalculateRect(start, end);

        // Assert
        Assert.Equal(10, left);
        Assert.Equal(20, top);
        Assert.Equal(100, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void CalculateRect_BottomRightToTopLeft_NormalizesCorrectly()
    {
        // Arrange
        var start = new Point(110, 70);
        var end = new Point(10, 20);

        // Act
        var (left, top, width, height) = _sut.CalculateRect(start, end);

        // Assert
        Assert.Equal(10, left);
        Assert.Equal(20, top);
        Assert.Equal(100, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void CalculateEllipse_SameLogicAsRect()
    {
        // Arrange
        var start = new Point(5, 5);
        var end = new Point(55, 55);

        // Act
        var rect = _sut.CalculateRect(start, end);
        var ellipse = _sut.CalculateEllipse(start, end);

        // Assert
        Assert.Equal(rect, ellipse);
    }

    [Fact]
    public void IsValidShapeSize_LargeEnough_ReturnsTrue()
    {
        // Arrange
        var start = new Point(0, 0);
        var end = new Point(10, 10);

        // Act
        var result = _sut.IsValidShapeSize(start, end);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidShapeSize_TooSmall_ReturnsFalse()
    {
        // Arrange
        var start = new Point(0, 0);
        var end = new Point(2, 2);

        // Act
        var result = _sut.IsValidShapeSize(start, end);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidShapeSize_ExactlyMinSide_ReturnsTrue()
    {
        // Arrange
        var start = new Point(0, 0);
        var end = new Point(4, 4);

        // Act
        var result = _sut.IsValidShapeSize(start, end);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidShapeSize_HorizontalLine_ValidByWidth()
    {
        // Arrange
        var start = new Point(0, 0);
        var end = new Point(100, 0);

        // Act
        var result = _sut.IsValidShapeSize(start, end);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidShapeSize_CustomMinSide_RespectsThreshold()
    {
        // Arrange
        var start = new Point(0, 0);

        // Act
        var tooSmall = _sut.IsValidShapeSize(start, new Point(5, 5), minSide: 10);
        var exactlyRight = _sut.IsValidShapeSize(start, new Point(10, 10), minSide: 10);

        // Assert
        Assert.False(tooSmall);
        Assert.True(exactlyRight);
    }

    [Fact]
    public void CalculateArrowHead_ReturnsThreePoints()
    {
        // Arrange
        var tail = new Point(0, 0);
        var tip = new Point(100, 0);

        // Act
        var pts = _sut.CalculateArrowHead(tail, tip);

        // Assert
        Assert.Equal(3, pts.Length);
    }

    [Fact]
    public void CalculateArrowHead_MiddlePointIsArrowTip()
    {
        // Arrange
        var tail = new Point(0, 0);
        var tip = new Point(100, 0);

        // Act
        var pts = _sut.CalculateArrowHead(tail, tip);

        // Assert
        Assert.Equal(tip, pts[1]);
    }

    [Fact]
    public void CalculateArrowHead_HeadPointsAreNearTip()
    {
        // Arrange
        var tail = new Point(0, 0);
        var tip = new Point(100, 0);
        const double MaxDist = 15.0;

        // Act
        var pts = _sut.CalculateArrowHead(tail, tip);

        // Assert
        Assert.True(Distance(pts[0], tip) <= MaxDist);
        Assert.True(Distance(pts[2], tip) <= MaxDist);
    }

    [Fact]
    public void CalculateArrowHead_Symmetric_AroundShaftAxis()
    {
        // Arrange
        var tail = new Point(0, 0);
        var tip = new Point(100, 0);

        // Act
        var pts = _sut.CalculateArrowHead(tail, tip);

        // Assert
        Assert.Equal(pts[0].X, pts[2].X, precision: 8);
        Assert.Equal(-pts[0].Y, pts[2].Y, precision: 8);
    }

    private static double Distance(Point a, Point b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
