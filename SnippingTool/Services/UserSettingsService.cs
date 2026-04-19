using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;

namespace SnippingTool.Services;

public sealed class UserSettingsService : IUserSettingsService
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";
    private const string LegacySettingsFolderName = "SnippingTool";
    private const string CurrentSettingsFolderName = "Pointframe";
    private readonly string _settingsPath;
    private readonly ILogger<UserSettingsService> _logger;
    private readonly object _syncRoot = new();

    public UserSettings Current { get; private set; }

    public UserSettingsService(ILogger<UserSettingsService> logger)
        : this(logger, GetSettingsPath())
    {
    }

    internal UserSettingsService(ILogger<UserSettingsService> logger, string settingsPath)
    {
        _logger = logger;
        _settingsPath = settingsPath;

        Current = Load();
    }

    private UserSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            _logger.LogDebug("No settings.json found — using defaults");
            return new UserSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<UserSettings>(json);
            if (loaded is not null)
            {
                _logger.LogInformation("Settings loaded from {Path}", _settingsPath);
                return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings.json — using defaults");
        }

        return new UserSettings();
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_syncRoot)
        {
            Persist(settings);
            Current = settings;
        }

        _logger.LogInformation("Settings saved to {Path}", _settingsPath);
    }

    public void Update(Action<UserSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_syncRoot)
        {
            var updated = Clone(Current);
            mutate(updated);
            Persist(updated);
            Current = updated;
        }

        _logger.LogInformation("Settings updated and saved to {Path}", _settingsPath);
    }

    private void Persist(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    private static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacySettingsPath = Path.Combine(localAppData, LegacySettingsFolderName, "settings.json");
        if (File.Exists(legacySettingsPath))
        {
            return legacySettingsPath;
        }

        return Path.Combine(localAppData, CurrentSettingsFolderName, "settings.json");
    }

    private static string GetSettingsPath()
    {
        var automationSettingsPath = Environment.GetEnvironmentVariable(AutomationSettingsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(automationSettingsPath))
        {
            return automationSettingsPath;
        }

        return GetDefaultSettingsPath();
    }

    private static UserSettings Clone(UserSettings settings) =>
        new()
        {
            ScreenshotSavePath = settings.ScreenshotSavePath,
            AutoSaveScreenshots = settings.AutoSaveScreenshots,
            RecordingOutputPath = settings.RecordingOutputPath,
            RecordMicrophone = settings.RecordMicrophone,
            RecordingMicrophoneDeviceName = settings.RecordingMicrophoneDeviceName,
            RecordingFps = settings.RecordingFps,
            GifFps = settings.GifFps,
            HudGapPixels = settings.HudGapPixels,
            RecordingCursorHighlightEnabled = settings.RecordingCursorHighlightEnabled,
            RecordingClickRippleEnabled = settings.RecordingClickRippleEnabled,
            RecordingCursorHighlightSize = settings.RecordingCursorHighlightSize,
            DefaultAnnotationColor = settings.DefaultAnnotationColor,
            DefaultStrokeThickness = settings.DefaultStrokeThickness,
            CaptureDelaySeconds = settings.CaptureDelaySeconds,
            RegionCaptureHotkey = settings.RegionCaptureHotkey,
            AutoUpdateCheckInterval = settings.AutoUpdateCheckInterval,
            LastAutoUpdateCheckUtc = settings.LastAutoUpdateCheckUtc,
            Theme = settings.Theme,
        };
}
