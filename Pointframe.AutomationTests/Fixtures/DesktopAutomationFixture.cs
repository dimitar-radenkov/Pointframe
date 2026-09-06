using System.Text.Json;
using Pointframe.Models;

namespace Pointframe.AutomationTests.Fixtures;

public class DesktopAutomationFixture : IDisposable
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";
    private const string AutomationOutputDirectoryEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OUTPUT_DIRECTORY";
    private const string AutomationOpenImagePathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_OPEN_IMAGE_PATH";
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

    public IReadOnlyDictionary<string, string> CreateEnvironmentVariables(bool includeOpenImageSamplePath = false)
    {
        Directory.CreateDirectory(OutputDirectory);

        var environmentVariables = new Dictionary<string, string>
        {
            [AutomationSettingsPathEnvironmentVariable] = SettingsPath,
            [AutomationOutputDirectoryEnvironmentVariable] = OutputDirectory,
        };

        if (includeOpenImageSamplePath)
        {
            environmentVariables[AutomationOpenImagePathEnvironmentVariable] = SampleOverlayPath;
        }

        return environmentVariables;
    }

    public UserSettings ReadSettings()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
        return settings ?? throw new InvalidOperationException("The automation settings file could not be deserialized.");
    }

    public void SeedSettings(
        bool autoSaveScreenshots,
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
            RecordingFps = recordingFps,
        };

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    public void EnsureRecordingBackendAvailable()
    {
        _ = ResolveRecordingBackendPath();
    }

    public string ResolveRecordingBackendPath()
    {
        var appDirectory = AppContext.BaseDirectory;
        var directPath = Path.Combine(appDirectory, "ffmpeg.exe");
        var bundledPath = Path.Combine(appDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(directPath) || File.Exists(bundledPath))
        {
            return File.Exists(directPath) ? directPath : bundledPath;
        }

        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is not null)
        {
            var binRoot = Path.Combine(repositoryRoot.FullName, "Pointframe", "bin");
            if (Directory.Exists(binRoot))
            {
                var builtBackend = Directory
                    .EnumerateFiles(binRoot, "ffmpeg.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(builtBackend))
                {
                    return builtBackend;
                }
            }
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathEntries.Any(pathEntry => File.Exists(Path.Combine(pathEntry, "ffmpeg.exe"))))
        {
            return "ffmpeg.exe";
        }

        throw new InvalidOperationException(
            "Recording smoke requires ffmpeg.exe for MP4 output, but it was not found next to the test app, under Assets\\ffmpeg, or on PATH.");
    }

    private static DirectoryInfo? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var appProjectDirectory = Path.Combine(directory.FullName, "Pointframe");
            var automationProjectDirectory = Path.Combine(directory.FullName, "Pointframe.AutomationTests");

            if (Directory.Exists(appProjectDirectory) && Directory.Exists(automationProjectDirectory))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
