namespace Pointframe.Engine;

public static class PointframePaths
{
    private const string AutomationDataDirectoryEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY";

    public static string LocalAppDataDirectory =>
        Environment.GetEnvironmentVariable(AutomationDataDirectoryEnvironmentVariable) is { Length: > 0 } overridePath
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pointframe");

    public static string PointframeDatabasePath =>
        Path.Combine(LocalAppDataDirectory, "pointframe.db");

    public static string DefaultStandaloneScreenshotDirectory =>
        Path.Combine(LocalAppDataDirectory, "Screenshots");
}
