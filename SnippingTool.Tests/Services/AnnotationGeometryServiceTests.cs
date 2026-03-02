using System.Windows;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public class AnnotationGeometryServiceTests
{
    private readonly AnnotationGeometryService _sut = new();

    [Fact]
    public void CalculateRect_TopLeftToBottomRight_ReturnsCorrectBounds()
    {
        var (left, top, width, height) = _sut.CalculateRect(new Point(10, 20), new Point(110, 70));
        Assert.Equal(10, left);
        Assert.Equal(20, top);
        Assert.Equal(100, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void CalculateRect_BottomRightToTopLeft_NormalizesCorrectly()
    {
        var (left, top, width, height) = _sut.CalculateRect(new Point(110, 70), new Point(10, 20));
        Assert.Equal(10, left);
        Assert.Equal(20, top);
        Assert.Equal(100, width);
        Assert.Equal(50, height);
    }

    [Fact]
    public void CalculateEllipse_SameLogicAsRect()
    {
        var rect = _sut.CalculateRect(new Point(5, 5), new Point(55, 55));
        var ellipse = _sut.CalculateEllipse(new Point(5, 5), new Point(55, 55));
        Assert.Equal(rect, ellipse);
    }

    [Fact]
    public void IsValidShapeSize_LargeEnough_ReturnsTrue()
    {
        Assert.True(_sut.IsValidShapeSize(new Point(0, 0), new Point(10, 10)));
    }

    [Fact]
    public void IsValidShapeSize_TooSmall_ReturnsFalse()
    {
        Assert.False(_sut.IsValidShapeSize(new Point(0, 0), new Point(2, 2)));
    }

    [Fact]
    public void IsValidShapeSize_ExactlyMinSide_ReturnsTrue()
    {
        Assert.True(_sut.IsValidShapeSize(new Point(0, 0), new Point(4, 4)));
    }

    [Fact]
    public void IsValidShapeSize_HorizontalLine_ValidByWidth()
    {
        // Height=0 but width=100 — valid for line/arrow shapes
        Assert.True(_sut.IsValidShapeSize(new Point(0, 0), new Point(100, 0)));
    }

    [Fact]
    public void IsValidShapeSize_CustomMinSide_RespectsThreshold()
    {
        Assert.False(_sut.IsValidShapeSize(new Point(0, 0), new Point(5, 5), minSide: 10));
        Assert.True(_sut.IsValidShapeSize(new Point(0, 0), new Point(10, 10), minSide: 10));
    }

    [Fact]
    public void CalculateArrowHead_ReturnsThreePoints()
    {
        var pts = _sut.CalculateArrowHead(new Point(0, 0), new Point(100, 0));
        Assert.Equal(3, pts.Length);
    }

    [Fact]
    public void CalculateArrowHead_MiddlePointIsArrowTip()
    {
        var tip = new Point(100, 0);
        var pts = _sut.CalculateArrowHead(new Point(0, 0), tip);
        Assert.Equal(tip, pts[1]);
    }

    [Fact]
    public void CalculateArrowHead_HeadPointsAreNearTip()
    {
        var tip = new Point(100, 0);
        var pts = _sut.CalculateArrowHead(new Point(0, 0), tip);

        // Both wing points must be within headLength distance of the tip
        const double maxDist = 15.0;
        Assert.True(Distance(pts[0], tip) <= maxDist);
        Assert.True(Distance(pts[2], tip) <= maxDist);
    }

    [Fact]
    public void CalculateArrowHead_Symmetric_AroundShaftAxis()
    {
        // Horizontal arrow → both wings should be equidistant from the shaft line (Y axis symmetry)
        var pts = _sut.CalculateArrowHead(new Point(0, 0), new Point(100, 0));
        Assert.Equal(pts[0].X, pts[2].X, precision: 8);
        Assert.Equal(-pts[0].Y, pts[2].Y, precision: 8); // symmetric above/below
    }

    private static double Distance(Point a, Point b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
