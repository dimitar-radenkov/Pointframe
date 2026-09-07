using System.Text.Json;

namespace Pointframe.AutomationTests.Support;

internal static class DesktopGatePolicyFactory
{
    public static string Create(string pointframeExecutablePath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointframeExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var executablePath = Path.GetFullPath(pointframeExecutablePath);
        var artifactRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(artifactRoot);

        var policyPath = Path.Combine(
            artifactRoot,
            $"desktop-testing-policy-{Guid.NewGuid():N}.json");
        var policy = new
        {
            schemaVersion = 1,
            artifactRoot,
            evidencePolicy = "All",
            profiles = new[]
            {
                new
                {
                    id = "pointframe",
                    executablePath,
                    arguments = Array.Empty<string>(),
                    workingDirectory = Path.GetDirectoryName(executablePath)!,
                    allowAttach = false,
                    allowedActions = new[]
                    {
                        "ListApps",
                        "StartTestSession",
                        "RestartApp",
                        "ObserveApp",
                        "FocusWindow",
                        "Click",
                        "PressKeys",
                        "Drag",
                        "EnterText",
                        "Invoke",
                        "CheckUi",
                        "Scroll",
                        "GetActionResult",
                        "GetTestReport",
                        "EndTestSession",
                    },
                    allowedGlobalHotkeys = new
                    {
                        capture = new[] { "CTRL", "SHIFT", "P" },
                    },
                    allowedShellSurfaces = new[]
                    {
                        "NotificationArea",
                        "NotificationOverflow",
                    },
                    allowMonitorObservation = true,
                },
            },
        };

        File.WriteAllText(
            policyPath,
            JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));
        return policyPath;
    }

    public static void Delete(string policyPath)
    {
        if (File.Exists(policyPath))
        {
            File.Delete(policyPath);
        }
    }
}
