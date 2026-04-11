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

    public void SeedSettings(
        bool autoSaveScreenshots,
        RecordingFormat recordingFormat = RecordingFormat.Mp4,
        int recordingFps = 20)
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
            RecordingFormat = recordingFormat,
            RecordingFps = recordingFps,
        };

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    public void EnsureRecordingBackendAvailable(RecordingFormat recordingFormat)
    {
        if (recordingFormat != RecordingFormat.Mp4)
        {
            return;
        }

        var appDirectory = AppContext.BaseDirectory;
        var directPath = Path.Combine(appDirectory, "ffmpeg.exe");
        var bundledPath = Path.Combine(appDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(directPath) || File.Exists(bundledPath))
        {
            return;
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathEntries.Any(pathEntry => File.Exists(Path.Combine(pathEntry, "ffmpeg.exe"))))
        {
            return;
        }

        throw new InvalidOperationException(
            "Recording smoke requires ffmpeg.exe for MP4 output, but it was not found next to the test app, under Assets\\ffmpeg, or on PATH.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
