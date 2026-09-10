using Pointframe.Engine;

namespace Pointframe.Services;

internal static class AppPaths
{
    public static string LocalAppDataDirectory =>
        PointframePaths.LocalAppDataDirectory;

    public static string LogsDirectory => System.IO.Path.Combine(LocalAppDataDirectory, "logs");

    public static string RollingLogPath => System.IO.Path.Combine(LogsDirectory, "pointframe-.log");

    public static string PointframeDatabasePath =>
        PointframePaths.PointframeDatabasePath;
}
