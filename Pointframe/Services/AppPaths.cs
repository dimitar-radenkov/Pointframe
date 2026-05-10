namespace Pointframe.Services;

internal static class AppPaths
{
    public static string LocalAppDataDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pointframe");

    public static string LogsDirectory => System.IO.Path.Combine(LocalAppDataDirectory, "logs");

    public static string RollingLogPath => System.IO.Path.Combine(LogsDirectory, "pointframe-.log");
}
