using System.Text.Json;
using SnippingTool.Models;

namespace SnippingTool.AutomationTests.Fixtures;

public class DesktopAutomationFixture : IDisposable
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";
    private const string AutomationOutputDirectoryEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OUTPUT_DIRECTORY";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SnippingTool.AutomationTests",
        Guid.NewGuid().ToString("N"));

    public string SettingsPath => Path.Combine(_tempDirectory, "settings.json");

    public string OutputDirectory => Path.Combine(_tempDirectory, "Output");

    public string ScreenshotOutputPath => Path.Combine(OutputDirectory, "Screenshots");

    public string RecordingOutputPath => Path.Combine(OutputDirectory, "Videos");

    public string SampleOverlayPath => Path.Combine(OutputDirectory, "automation-sample-overlay.png");

    public IReadOnlyDictionary<string, string> CreateEnvironmentVariables()
    {
        Directory.CreateDirectory(OutputDirectory);

        return new Dictionary<string, string>
        {
            [AutomationSettingsPathEnvironmentVariable] = SettingsPath,
            [AutomationOutputDirectoryEnvironmentVariable] = OutputDirectory,
        };
    }

    public UserSettings ReadSettings()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
        return settings ?? throw new InvalidOperationException("The automation settings file could not be deserialized.");
    }

    public void SeedSettings(bool autoSaveScreenshots)
    {
        if (Directory.Exists(OutputDirectory))
        {
            Directory.Delete(OutputDirectory, recursive: true);
        }

        Directory.CreateDirectory(ScreenshotOutputPath);
        Directory.CreateDirectory(RecordingOutputPath);

        var settings = new UserSettings
        {
            AutoSaveScreenshots = autoSaveScreenshots,
            ScreenshotSavePath = ScreenshotOutputPath,
            RecordingOutputPath = RecordingOutputPath,
        };

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
