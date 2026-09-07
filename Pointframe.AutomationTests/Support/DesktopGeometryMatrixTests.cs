using Xunit;

namespace Pointframe.AutomationTests.Support;

public sealed class DesktopGeometryMatrixTests
{
    [Fact]
    public void DeclaresRequiredDpiAndTopologyCells()
    {
        Assert.Contains(DesktopGeometryMatrix.DeclaredCells, cell => cell.DpiPercent == 100);
        Assert.Contains(DesktopGeometryMatrix.DeclaredCells, cell => cell.DpiPercent == 125);
        Assert.Contains(DesktopGeometryMatrix.DeclaredCells, cell => cell.DpiPercent == 150);
        Assert.Contains(DesktopGeometryMatrix.DeclaredCells, cell => cell.DpiPercent == 200);
        Assert.Contains(DesktopGeometryMatrix.DeclaredCells, cell => cell.MixedDpi);
        Assert.Contains(DesktopGeometryMatrix.DeclaredCells, cell => cell.NegativeOrigin);
    }

    [Fact]
    public void UnknownCellsAreNotReportedAsExecuted()
    {
        Assert.False(DesktopGeometryMatrix.IsDeclared("not-run"));
    }
}
