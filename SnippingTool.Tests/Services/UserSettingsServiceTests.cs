using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class UserSettingsServiceTests : IDisposable
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SnippingTool.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Update_ClonesCurrentAppliesMutationAndPersistsChanges()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);
        var original = sut.Current;
        var expectedTimestamp = new DateTime(2026, 3, 18, 12, 0, 0, DateTimeKind.Utc);

        sut.Update(settings =>
        {
            settings.LastAutoUpdateCheckUtc = expectedTimestamp;
            settings.AutoSaveScreenshots = true;
        });

        Assert.NotSame(original, sut.Current);
        Assert.Null(original.LastAutoUpdateCheckUtc);
        Assert.False(original.AutoSaveScreenshots);
        Assert.Equal(expectedTimestamp, sut.Current.LastAutoUpdateCheckUtc);
        Assert.True(sut.Current.AutoSaveScreenshots);

        var persisted = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath));

        Assert.NotNull(persisted);
        Assert.Equal(expectedTimestamp, persisted!.LastAutoUpdateCheckUtc);
        Assert.True(persisted.AutoSaveScreenshots);
    }

    [Fact]
    public void Update_WhenMutationThrows_DoesNotChangeCurrentOrPersistFile()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);
        var original = sut.Current;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            sut.Update(_ => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", exception.Message);
        Assert.Same(original, sut.Current);
        Assert.False(File.Exists(settingsPath));
    }

    [Fact]
    public void Load_WhenFileMissing_UsesDefaultSettings()
    {
        var settingsPath = Path.Combine(_tempDirectory, "missing.json");

        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);

        Assert.NotNull(sut.Current);
        Assert.False(File.Exists(settingsPath));
    }

    [Fact]
    public void Load_WhenJsonIsInvalid_UsesDefaultSettings()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(settingsPath, "not-json");

        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);

        Assert.NotNull(sut.Current);
    }

    [Fact]
    public void Save_PersistsProvidedSettingsAndUpdatesCurrent()
    {
        var settingsPath = Path.Combine(_tempDirectory, "nested", "settings.json");
        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);
        var settings = new UserSettings
        {
            AutoSaveScreenshots = true,
            CaptureDelaySeconds = 3,
            RecordingFps = 60,
        };

        sut.Save(settings);

        Assert.Same(settings, sut.Current);
        Assert.True(File.Exists(settingsPath));
        var persisted = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath));
        Assert.NotNull(persisted);
        Assert.True(persisted!.AutoSaveScreenshots);
        Assert.Equal(3, persisted.CaptureDelaySeconds);
        Assert.Equal(60, persisted.RecordingFps);
    }

    [Fact]
    public void Constructor_WhenAutomationSettingsPathEnvironmentVariableIsSet_UsesOverridePath()
    {
        var settingsPath = Path.Combine(_tempDirectory, "automation", "settings.json");
        var originalValue = Environment.GetEnvironmentVariable(AutomationSettingsPathEnvironmentVariable);
        Environment.SetEnvironmentVariable(AutomationSettingsPathEnvironmentVariable, settingsPath);

        try
        {
            var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance);
            var settings = new UserSettings
            {
                AutoSaveScreenshots = true,
            };

            sut.Save(settings);

            Assert.Same(settings, sut.Current);
            Assert.True(File.Exists(settingsPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AutomationSettingsPathEnvironmentVariable, originalValue);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
