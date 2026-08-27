using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.Services;

// Guards the documented settings trap: a UserSettings property that is not carried
// through every persistence path (service save/load, Update's clone, SettingsViewModel.Save)
// is silently dropped. CreateFullyPopulatedSettings must set every property to a
// non-default value; the guard test enforces that, and the round-trip tests then prove
// no path drops a value. When adding a new setting, extend CreateFullyPopulatedSettings —
// the guard test fails until you do.
public sealed class SettingsRoundTripTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "SnippingTool.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateFullyPopulatedSettings_DiffersFromDefaultsOnEveryProperty()
    {
        var populated = CreateFullyPopulatedSettings();
        var defaults = new UserSettings();

        foreach (var property in typeof(UserSettings).GetProperties())
        {
            var populatedValue = property.GetValue(populated);
            var defaultValue = property.GetValue(defaults);

            if (populatedValue is WatermarkSettings populatedWatermark)
            {
                AssertWatermarkFullyPopulated(property.Name, populatedWatermark);
                continue;
            }

            Assert.False(
                JsonSerializer.Serialize(populatedValue) == JsonSerializer.Serialize(defaultValue),
                $"CreateFullyPopulatedSettings must set UserSettings.{property.Name} to a non-default value " +
                "so the round-trip tests can detect a persistence path dropping it.");
        }
    }

    [Fact]
    public void SaveAndReload_PreservesEveryProperty()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var populated = CreateFullyPopulatedSettings();

        new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath).Save(populated);
        var reloaded = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);

        Assert.Equal(ToJson(populated), ToJson(reloaded.Current));
    }

    [Fact]
    public void Update_WithNoOpMutation_PreservesEveryProperty()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var sut = new UserSettingsService(NullLogger<UserSettingsService>.Instance, settingsPath);
        sut.Save(CreateFullyPopulatedSettings());
        var before = ToJson(sut.Current);

        sut.Update(_ => { });

        Assert.Equal(before, ToJson(sut.Current));
    }

    [Fact]
    public void SettingsViewModel_Save_PreservesEveryProperty()
    {
        var populated = CreateFullyPopulatedSettings();
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(populated);
        UserSettings? saved = null;
        settingsService.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { populated.RecordingMicrophoneDeviceName! } &&
            service.GetDefaultCaptureDeviceName() == populated.RecordingMicrophoneDeviceName);
        var vm = new SettingsViewModel(
            settingsService.Object,
            Mock.Of<IThemeService>(),
            Mock.Of<IDialogService>(),
            microphoneService);

        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(ToJson(populated), ToJson(saved!));
    }

    // Values must survive SettingsViewModel's load/save transformations unchanged:
    // colors in canonical #AARRGGBB uppercase form, cursor highlight size inside the
    // 8..96 clamp range, microphone device name present in the mocked device list, and
    // ScreenshotWatermark equal to VideoWatermark (the VM edits one shared watermark state).
    private static UserSettings CreateFullyPopulatedSettings()
    {
        return new UserSettings
        {
            ScreenshotSavePath = @"C:\changed\screenshots",
            AutoSaveScreenshots = false,
            SmartRedactionEnabled = false,
            SmartRedactionExcludedBuiltInTypes =
            [
                SensitiveDataType.Email,
                SensitiveDataType.JwtLike,
            ],
            CustomRedactionPatterns =
            [
                new SmartRedactionPattern
                {
                    Name = "Customer ID",
                    Pattern = @"\bCUST-\d{5}\b",
                    IsEnabled = true,
                },
                new SmartRedactionPattern
                {
                    Name = "Secret Label",
                    Pattern = @"\bSECRET:\s*\w+\b",
                    IsEnabled = false,
                },
            ],
            RecordingOutputPath = @"C:\changed\videos",
            RecordMicrophone = false,
            RecordingMicrophoneDeviceName = "Changed Mic",
            RecordingFps = 60,
            GifFps = 15,
            HudGapPixels = 12,
            RecordingCursorHighlightEnabled = false,
            RecordingClickRippleEnabled = false,
            RecordingCursorHighlightSize = 42d,
            DefaultAnnotationColor = "#FF336699",
            DefaultStrokeThickness = 5.5,
            CaptureDelaySeconds = 5,
            RegionCaptureHotkey = 0x41,
            RegionCaptureHotkeyModifiers = HotkeyModifiers.Alt,
            WholeScreenRecordHotkey = 0x42,
            WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Ctrl,
            CleanWindowCaptureHotkey = 0x44,
            CleanWindowCaptureHotkeyModifiers = HotkeyModifiers.Alt,
            OverlayCopyHotkey = 0x31,
            OverlayCopyHotkeyModifiers = HotkeyModifiers.Alt,
            OverlaySaveAsHotkey = 0x32,
            OverlaySaveAsHotkeyModifiers = HotkeyModifiers.Alt,
            OverlayUndoHotkey = 0x33,
            OverlayUndoHotkeyModifiers = HotkeyModifiers.Alt,
            OverlayRedoHotkey = 0x34,
            OverlayRedoHotkeyModifiers = HotkeyModifiers.Alt,
            OverlayToggleShortcutsHotkey = 0x35,
            OverlayToggleShortcutsHotkeyModifiers = HotkeyModifiers.Alt,
            OverlayCloseHotkey = 0x36,
            OverlayCloseHotkeyModifiers = HotkeyModifiers.Alt,
            AutoUpdateCheckInterval = UpdateCheckInterval.EveryDay,
            LastAutoUpdateCheckUtc = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc),
            Theme = AppTheme.Dark,
            StylePresets =
            [
                new AnnotationStylePreset { Name = "Changed A", Color = "#FF112233", StrokeThickness = 4.5 },
                new AnnotationStylePreset { Name = "Changed B", Color = "#FF445566", StrokeThickness = 6.5 },
            ],
            ScreenshotWatermark = CreatePopulatedWatermark<ScreenshotWatermarkSettings>(),
            VideoWatermark = CreatePopulatedWatermark<VideoWatermarkSettings>(),
            InstallId = "changed-install-id",
            InstallCreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            FirstCaptureCompletedTracked = true,
            FirstRecordingCompletedTracked = true,
        };
    }

    private static TWatermark CreatePopulatedWatermark<TWatermark>()
        where TWatermark : WatermarkSettings, new()
    {
        return new TWatermark
        {
            Enabled = true,
            TextTemplate = WatermarkTextTemplate.TimeOnly,
            Position = WatermarkPosition.TopLeft,
            FontSize = 24,
            ColorHex = "#FFABCDEF",
            BackgroundEnabled = false,
            Opacity = 0.7,
            Margin = 21,
            ApplyToCopy = false,
            ApplyToSave = false,
        };
    }

    private static void AssertWatermarkFullyPopulated(string propertyName, WatermarkSettings populated)
    {
        var defaults = new WatermarkSettings();
        foreach (var property in typeof(WatermarkSettings).GetProperties())
        {
            Assert.False(
                JsonSerializer.Serialize(property.GetValue(populated)) == JsonSerializer.Serialize(property.GetValue(defaults)),
                $"CreateFullyPopulatedSettings must set UserSettings.{propertyName}.{property.Name} to a non-default value " +
                "so the round-trip tests can detect a persistence path dropping it.");
        }
    }

    private static string ToJson(UserSettings settings) =>
        JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
