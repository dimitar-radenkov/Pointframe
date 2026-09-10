using System.Text.Json;

namespace Pointframe.Engine;

public sealed class StandaloneCaptureLibrarySources : ICaptureLibrarySources
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";

    public IReadOnlyList<string> GetImportRoots()
    {
        var roots = new List<string> { PointframePaths.DefaultStandaloneScreenshotDirectory };
        var configuredRoot = TryReadScreenshotSavePath();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            roots.Add(configuredRoot);
        }

        return roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? TryReadScreenshotSavePath()
    {
        var settingsPath = Environment.GetEnvironmentVariable(AutomationSettingsPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            settingsPath = Path.Combine(PointframePaths.LocalAppDataDirectory, "settings.json");
        }

        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.TryGetProperty("ScreenshotSavePath", out var screenshotSavePath)
                ? screenshotSavePath.GetString()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
