namespace Pointframe.Engine;

public static class PointframePaths
{
    public static string LocalAppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe");

    public static string PointframeDatabasePath =>
        Path.Combine(LocalAppDataDirectory, "pointframe.db");

    public static string DefaultStandaloneScreenshotDirectory =>
        Path.Combine(LocalAppDataDirectory, "Screenshots");
}
