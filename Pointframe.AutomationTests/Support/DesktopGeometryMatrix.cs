namespace Pointframe.AutomationTests.Support;

public sealed record DesktopGeometryCell(
    string Name,
    int DpiPercent,
    bool MixedDpi,
    bool NegativeOrigin);

public static class DesktopGeometryMatrix
{
    public static IReadOnlyList<DesktopGeometryCell> DeclaredCells { get; } =
    [
        new("100%", 100, false, false),
        new("125%", 125, false, false),
        new("150%", 150, false, false),
        new("200%", 200, false, false),
        new("mixed-dpi", 150, true, false),
        new("negative-origin", 100, false, true),
    ];

    public static bool IsDeclared(string name) =>
        DeclaredCells.Any(cell => string.Equals(cell.Name, name, StringComparison.OrdinalIgnoreCase));
}
