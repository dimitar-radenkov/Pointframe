using System.Text.Json;
using Moq;
using Pointframe.Services;
using Pointframe.Tests.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

// Pins down which persisted properties each reset path touches, starting from settings where
// every property differs from its default. Written before restructuring SettingsViewModel's
// reset and capture-mode code so the refactor can be checked against the old behavior.
public sealed class SettingsViewModelCharacterizationTests
{
    private static readonly string[] OverlayShortcutProperties =
    [
        nameof(UserSettings.OverlayCopyHotkey), nameof(UserSettings.OverlayCopyHotkeyModifiers),
        nameof(UserSettings.OverlaySaveAsHotkey), nameof(UserSettings.OverlaySaveAsHotkeyModifiers),
        nameof(UserSettings.OverlayUndoHotkey), nameof(UserSettings.OverlayUndoHotkeyModifiers),
        nameof(UserSettings.OverlayRedoHotkey), nameof(UserSettings.OverlayRedoHotkeyModifiers),
        nameof(UserSettings.OverlayToggleShortcutsHotkey), nameof(UserSettings.OverlayToggleShortcutsHotkeyModifiers),
        nameof(UserSettings.OverlayCloseHotkey), nameof(UserSettings.OverlayCloseHotkeyModifiers),
    ];

    public static TheoryData<SettingsSection, string[]> SectionResets => new()
    {
        {
            SettingsSection.Capture,
            [
                nameof(UserSettings.ScreenshotSavePath), nameof(UserSettings.AutoSaveScreenshots),
                nameof(UserSettings.CaptureDelaySeconds),
                nameof(UserSettings.RegionCaptureHotkey), nameof(UserSettings.RegionCaptureHotkeyModifiers),
                nameof(UserSettings.ScreenshotWatermark), nameof(UserSettings.VideoWatermark),
            ]
        },
        {
            SettingsSection.Recording,
            [
                nameof(UserSettings.RecordingOutputPath), nameof(UserSettings.RecordMicrophone),
                nameof(UserSettings.RecordingTranscriptEnabled), nameof(UserSettings.GifFps),
                nameof(UserSettings.RecordingCursorHighlightEnabled), nameof(UserSettings.RecordingClickRippleEnabled),
                nameof(UserSettings.RecordingCursorHighlightSize),
                nameof(UserSettings.WholeScreenRecordHotkey), nameof(UserSettings.WholeScreenRecordHotkeyModifiers),
                nameof(UserSettings.CleanWindowCaptureHotkey), nameof(UserSettings.CleanWindowCaptureHotkeyModifiers),
            ]
        },
        {
            SettingsSection.Annotation,
            [
                nameof(UserSettings.DefaultAnnotationColor), nameof(UserSettings.DefaultStrokeThickness),
                nameof(UserSettings.StylePresets),
            ]
        },
        {
            SettingsSection.Sharing,
            [
                nameof(UserSettings.ShareDestinationUrl), nameof(UserSettings.ShareFileFieldName),
                nameof(UserSettings.ShareHeaders), nameof(UserSettings.ShareResponseLinkPath),
                nameof(UserSettings.ShareTimeoutSeconds),
            ]
        },
        {
            SettingsSection.App,
            [nameof(UserSettings.AutoUpdateCheckInterval), nameof(UserSettings.Theme)]
        },
        { SettingsSection.Shortcuts, OverlayShortcutProperties },
    };

    [Theory]
    [MemberData(nameof(SectionResets))]
    public void ResetCurrentSection_ThenSave_ChangesExactlyThatSectionsProperties(SettingsSection section, string[] expected)
    {
        var (populated, saved) = SaveAfter(vm =>
        {
            vm.SelectedSection = section;
            vm.ResetCurrentSectionCommand.Execute(null);
        });

        Assert.Equal(expected.Order(), ChangedProperties(populated, saved).Order());
    }

    [Fact]
    public void RestoreDefaults_ThenSave_WritesDefaultsExceptActivationFieldsAndMicrophone()
    {
        var (populated, saved) = SaveAfter(vm => vm.RestoreDefaultsCommand.Execute(null));

        var expected = new UserSettings
        {
            InstallId = populated.InstallId,
            InstallCreatedUtc = populated.InstallCreatedUtc,
            FirstCaptureCompletedTracked = populated.FirstCaptureCompletedTracked,
            FirstRecordingCompletedTracked = populated.FirstRecordingCompletedTracked,
            // The only available device, so restoring the default (null) resolves back to it.
            RecordingMicrophoneDeviceName = populated.RecordingMicrophoneDeviceName,
            VideoWatermark = new VideoWatermarkSettings(),
        };

        Assert.Equal(ToJson(expected), ToJson(saved));
    }

    // The window shows no control for the watermark's color, background, opacity, or margin,
    // so a reset must clear them directly or they survive it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CaptureReset_ResetsWatermarkStyleTheWindowDoesNotShow(bool restoreAllDefaults)
    {
        var (_, saved) = SaveAfter(vm =>
        {
            if (restoreAllDefaults)
            {
                vm.RestoreDefaultsCommand.Execute(null);
            }
            else
            {
                vm.SelectedSection = SettingsSection.Capture;
                vm.ResetCurrentSectionCommand.Execute(null);
            }
        });

        var defaults = new WatermarkSettings();
        foreach (var watermark in new WatermarkSettings?[] { saved.ScreenshotWatermark, saved.VideoWatermark })
        {
            Assert.NotNull(watermark);
            Assert.Equal(defaults.ColorHex, watermark!.ColorHex);
            Assert.Equal(defaults.BackgroundEnabled, watermark.BackgroundEnabled);
            Assert.Equal(defaults.Opacity, watermark.Opacity);
            Assert.Equal(defaults.Margin, watermark.Margin);
        }
    }

    [Fact]
    public void RestoreDefaults_StopsEveryHotkeyCapture()
    {
        var vm = CreateVm(new UserSettings());
        vm.IsRecordingHotkey = true;
        vm.IsCapturingWholeScreenRecordHotkey = true;
        vm.IsCapturingCleanWindowCaptureHotkey = true;
        vm.StartCapturingOverlayShortcutCommand.Execute("OverlayUndo");
        vm.OverlayShortcutConflictMessage = "conflict";

        vm.RestoreDefaultsCommand.Execute(null);

        AssertCaptureState(vm, region: false, wholeScreen: false, cleanWindow: false, overlayTarget: null);
    }

    [Fact]
    public void StartRecordingHotkey_StopsEveryOtherCapture()
    {
        var vm = CreateVmCapturingEverything();

        vm.StartRecordingHotkeyCommand.Execute(null);

        AssertCaptureState(vm, region: true, wholeScreen: false, cleanWindow: false, overlayTarget: null);
    }

    [Fact]
    public void StartCapturingWholeScreenRecordHotkey_StopsEveryOtherCapture()
    {
        var vm = CreateVmCapturingEverything();

        vm.StartCapturingWholeScreenRecordHotkeyCommand.Execute(null);

        AssertCaptureState(vm, region: false, wholeScreen: true, cleanWindow: false, overlayTarget: null);
    }

    [Fact]
    public void StartCapturingCleanWindowCaptureHotkey_StopsEveryOtherCapture()
    {
        var vm = CreateVmCapturingEverything();

        vm.StartCapturingCleanWindowCaptureHotkeyCommand.Execute(null);

        AssertCaptureState(vm, region: false, wholeScreen: false, cleanWindow: true, overlayTarget: null);
    }

    [Fact]
    public void StartCapturingOverlayShortcut_StopsEveryOtherCaptureAndClearsConflict()
    {
        var vm = CreateVmCapturingEverything();

        vm.StartCapturingOverlayShortcutCommand.Execute("OverlayRedo");

        AssertCaptureState(vm, region: false, wholeScreen: false, cleanWindow: false, overlayTarget: "OverlayRedo");
        Assert.Equal("Redo", vm.OverlayShortcutCaptureDisplayName);
    }

    [Fact]
    public void HotkeyResetCommands_RestoreUserSettingsDefaultsAndStopCapture()
    {
        var defaults = new UserSettings();
        var vm = CreateVm(SettingsRoundTripTests.CreateFullyPopulatedSettings());
        vm.IsRecordingHotkey = true;
        vm.IsCapturingWholeScreenRecordHotkey = true;
        vm.IsCapturingCleanWindowCaptureHotkey = true;

        vm.ResetHotkeyCommand.Execute(null);
        vm.ResetRecordHotkeyCommand.Execute(null);
        vm.ResetCleanWindowCaptureHotkeyCommand.Execute(null);

        Assert.Equal(defaults.RegionCaptureHotkey, vm.RegionCaptureHotkey);
        Assert.Equal(defaults.RegionCaptureHotkeyModifiers, vm.RegionCaptureHotkeyModifiers);
        Assert.Equal(defaults.WholeScreenRecordHotkey, vm.WholeScreenRecordHotkey);
        Assert.Equal(defaults.WholeScreenRecordHotkeyModifiers, vm.WholeScreenRecordHotkeyModifiers);
        Assert.Equal(defaults.CleanWindowCaptureHotkey, vm.CleanWindowCaptureHotkey);
        Assert.Equal(defaults.CleanWindowCaptureHotkeyModifiers, vm.CleanWindowCaptureHotkeyModifiers);
        Assert.False(vm.IsRecordingHotkey);
        Assert.False(vm.IsCapturingWholeScreenRecordHotkey);
        Assert.False(vm.IsCapturingCleanWindowCaptureHotkey);
    }

    [Fact]
    public void AddPreset_UsesDefaultColorAsArgbHex()
    {
        var vm = CreateVm(new UserSettings { StylePresets = [] });
        vm.DefaultAnnotationColor = System.Windows.Media.Color.FromArgb(0x80, 0x0A, 0xB0, 0xFF);

        vm.AddPresetCommand.Execute(null);

        Assert.Equal("#800AB0FF", vm.StylePresets[^1].ToModel().Color);
    }

    private static (UserSettings Populated, UserSettings Saved) SaveAfter(Action<SettingsViewModel> act)
    {
        var populated = SettingsRoundTripTests.CreateFullyPopulatedSettings();
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(populated);
        UserSettings? saved = null;
        settingsService.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = CreateVm(populated, settingsService);

        act(vm);
        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        return (populated, saved!);
    }

    private static SettingsViewModel CreateVmCapturingEverything()
    {
        var vm = CreateVm(new UserSettings());
        vm.StartCapturingOverlayShortcutCommand.Execute("OverlayUndo");
        vm.OverlayShortcutConflictMessage = "conflict";
        vm.IsRecordingHotkey = true;
        vm.IsCapturingWholeScreenRecordHotkey = true;
        vm.IsCapturingCleanWindowCaptureHotkey = true;
        return vm;
    }

    private static SettingsViewModel CreateVm(UserSettings settings, Mock<IUserSettingsService>? settingsService = null)
    {
        if (settingsService is null)
        {
            settingsService = new Mock<IUserSettingsService>();
            settingsService.SetupGet(s => s.Current).Returns(settings);
        }

        var deviceName = settings.RecordingMicrophoneDeviceName ?? "Studio Mic";
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { deviceName } &&
            service.GetDefaultCaptureDeviceName() == deviceName);
        return new SettingsViewModel(settingsService.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), microphoneService);
    }

    private static void AssertCaptureState(SettingsViewModel vm, bool region, bool wholeScreen, bool cleanWindow, string? overlayTarget)
    {
        Assert.Equal(region, vm.IsRecordingHotkey);
        Assert.Equal(wholeScreen, vm.IsCapturingWholeScreenRecordHotkey);
        Assert.Equal(cleanWindow, vm.IsCapturingCleanWindowCaptureHotkey);
        Assert.Equal(overlayTarget is not null, vm.IsCapturingOverlayShortcut);
        Assert.Equal(overlayTarget ?? string.Empty, vm.OverlayShortcutCaptureTarget);
        Assert.Equal(string.Empty, vm.OverlayShortcutConflictMessage);
        if (overlayTarget is null)
        {
            Assert.Equal(string.Empty, vm.OverlayShortcutCaptureDisplayName);
        }
    }

    private static IEnumerable<string> ChangedProperties(UserSettings before, UserSettings after) =>
        typeof(UserSettings).GetProperties()
            .Where(p => JsonSerializer.Serialize(p.GetValue(before)) != JsonSerializer.Serialize(p.GetValue(after)))
            .Select(p => p.Name);

    private static string ToJson(UserSettings settings) =>
        JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
}
