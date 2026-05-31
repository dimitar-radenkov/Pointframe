using Moq;
using System.Reflection;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private static IMicrophoneDeviceService CreateMicrophoneDeviceService(
        IReadOnlyList<string>? availableDeviceNames = null,
        string? defaultDeviceName = "Studio Mic")
    {
        availableDeviceNames ??= ["Studio Mic", "USB Mic"];
        return Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == availableDeviceNames &&
            service.GetDefaultCaptureDeviceName() == defaultDeviceName);
    }

    private static SettingsViewModel CreateVm(
        UserSettings? settings = null,
        IDialogService? dialogService = null,
        IMicrophoneDeviceService? microphoneDeviceService = null)
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(settings ?? new UserSettings());
        return new SettingsViewModel(
            mock.Object,
            Mock.Of<IThemeService>(),
            dialogService ?? Mock.Of<IDialogService>(),
            microphoneDeviceService ?? CreateMicrophoneDeviceService());
    }

    [Fact]
    public void UserSettings_Default_CaptureDelaySeconds_IsZero()
    {
        // Arrange
        var settings = new UserSettings();

        // Act — default value, nothing to act on

        // Assert
        Assert.Equal(0, settings.CaptureDelaySeconds);
    }

    [Fact]
    public void Save_PersistsOverlayShortcutSettings()
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());

        vm.OverlayCopyHotkey = 0x58u; // X
        vm.OverlayCopyHotkeyModifiers = HotkeyModifiers.Alt;
        vm.OverlaySaveAsHotkey = 0x41u; // A
        vm.OverlaySaveAsHotkeyModifiers = HotkeyModifiers.Ctrl;

        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(0x58u, saved!.OverlayCopyHotkey);
        Assert.Equal(HotkeyModifiers.Alt, saved.OverlayCopyHotkeyModifiers);
        Assert.Equal(0x41u, saved.OverlaySaveAsHotkey);
        Assert.Equal(HotkeyModifiers.Ctrl, saved.OverlaySaveAsHotkeyModifiers);
    }

    [Fact]
    public void StartCapturingOverlayShortcutCommand_SetsCaptureState()
    {
        var vm = CreateVm();

        vm.StartCapturingOverlayShortcutCommand.Execute("OverlayRedo");

        Assert.True(vm.IsCapturingOverlayShortcut);
        Assert.Equal("OverlayRedo", vm.OverlayShortcutCaptureTarget);
        Assert.Equal("Redo", vm.OverlayShortcutCaptureDisplayName);
    }

    [Fact]
    public void ApplyOverlayShortcutCapture_WhenDuplicate_ShowsConflict_AndKeepsCaptureMode()
    {
        var vm = CreateVm();
        vm.StartCapturingOverlayShortcutCommand.Execute("OverlaySaveAs");

        var applyMethod = typeof(SettingsViewModel).GetMethod("ApplyOverlayShortcutCapture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(applyMethod);
        applyMethod!.Invoke(vm, [0x43u, HotkeyModifiers.Ctrl]); // duplicates default OverlayCopy (Ctrl+C)

        Assert.True(vm.IsCapturingOverlayShortcut);
        Assert.True(vm.HasOverlayShortcutConflict);
        Assert.Contains("already assigned", vm.OverlayShortcutConflictMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartCapturingOverlayShortcutCommand_ClearsOtherCaptureModes()
    {
        var vm = CreateVm();
        vm.StartRecordingHotkeyCommand.Execute(null);
        vm.StartCapturingWholeScreenRecordHotkeyCommand.Execute(null);

        vm.StartCapturingOverlayShortcutCommand.Execute("OverlayUndo");

        Assert.False(vm.IsRecordingHotkey);
        Assert.False(vm.IsCapturingWholeScreenRecordHotkey);
        Assert.True(vm.IsCapturingOverlayShortcut);
        Assert.Equal("OverlayUndo", vm.OverlayShortcutCaptureTarget);
    }

    [Fact]
    public void ApplyOverlayShortcutCapture_WhenUnique_AssignsAndExitsCaptureMode()
    {
        var vm = CreateVm();
        vm.StartCapturingOverlayShortcutCommand.Execute("OverlayUndo");

        var applyMethod = typeof(SettingsViewModel).GetMethod("ApplyOverlayShortcutCapture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(applyMethod);
        applyMethod!.Invoke(vm, [0x55u, HotkeyModifiers.Alt]); // Alt+U

        Assert.Equal(0x55u, vm.OverlayUndoHotkey);
        Assert.Equal(HotkeyModifiers.Alt, vm.OverlayUndoHotkeyModifiers);
        Assert.False(vm.IsCapturingOverlayShortcut);
        Assert.False(vm.HasOverlayShortcutConflict);
        Assert.Equal(string.Empty, vm.OverlayShortcutCaptureTarget);
    }

    [Fact]
    public void LoadsFromSettings_CaptureDelaySeconds()
    {
        // Arrange
        var vm = CreateVm(new UserSettings { CaptureDelaySeconds = 5 });

        // Act — value loaded during construction, nothing further to act on

        // Assert
        Assert.Equal(5, vm.CaptureDelaySeconds);
    }

    [Fact]
    public void Save_PersistsCaptureDelaySeconds()
    {
        // Arrange
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());
        vm.CaptureDelaySeconds = 10;

        // Act
        vm.SaveCommand.Execute(null);

        // Assert
        Assert.Equal(10, saved?.CaptureDelaySeconds);
    }

    [Fact]
    public void CaptureDelaySeconds_PropertyChanged_Fired()
    {
        // Arrange
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.CaptureDelaySeconds = 3;

        // Assert
        Assert.Contains(nameof(vm.CaptureDelaySeconds), raised);
    }

    [Fact]
    public void UserSettings_Default_RegionCaptureHotkey_IsPrintScreen()
    {
        // Arrange
        var settings = new UserSettings();

        // Assert
        Assert.Equal(0x2Cu, settings.RegionCaptureHotkey);
        Assert.Equal(HotkeyModifiers.None, settings.RegionCaptureHotkeyModifiers);
    }

    [Fact]
    public void LoadsFromSettings_RegionCaptureHotkey()
    {
        // Arrange
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0x41, RegionCaptureHotkeyModifiers = HotkeyModifiers.Ctrl }); // Ctrl+A

        // Assert
        Assert.Equal(0x41u, vm.RegionCaptureHotkey);
        Assert.Equal(HotkeyModifiers.Ctrl, vm.RegionCaptureHotkeyModifiers);
    }

    [Fact]
    public void Save_PersistsRegionCaptureHotkey()
    {
        // Arrange
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());
        vm.RegionCaptureHotkey = 0x42u; // 'B'
        vm.RegionCaptureHotkeyModifiers = HotkeyModifiers.Shift;

        // Act
        vm.SaveCommand.Execute(null);

        // Assert
        Assert.Equal(0x42u, saved?.RegionCaptureHotkey);
        Assert.Equal(HotkeyModifiers.Shift, saved?.RegionCaptureHotkeyModifiers);
    }

    [Fact]
    public void RegionCaptureHotkey_PropertyChanged_Fired()
    {
        // Arrange
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.RegionCaptureHotkey = 0x43u; // 'C'

        // Assert
        Assert.Contains(nameof(vm.RegionCaptureHotkey), raised);
    }

    [Fact]
    public void IsRecordingHotkey_DefaultsFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.IsRecordingHotkey);
    }

    [Fact]
    public void StartRecordingHotkeyCommand_SetsIsRecordingHotkeyTrue()
    {
        var vm = CreateVm();
        vm.StartRecordingHotkeyCommand.Execute(null);
        Assert.True(vm.IsRecordingHotkey);
    }

    [Fact]
    public void ResetHotkeyCommand_RestoresPrintScreenAndCancelsRecording()
    {
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0x41, RegionCaptureHotkeyModifiers = HotkeyModifiers.Ctrl });
        vm.StartRecordingHotkeyCommand.Execute(null);

        vm.ResetHotkeyCommand.Execute(null);

        Assert.Equal(0x2Cu, vm.RegionCaptureHotkey);
        Assert.Equal(HotkeyModifiers.None, vm.RegionCaptureHotkeyModifiers);
        Assert.False(vm.IsRecordingHotkey);
    }

    [Fact]
    public void IsRecordingHotkey_PropertyChanged_Fired()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.StartRecordingHotkeyCommand.Execute(null);

        Assert.Contains(nameof(vm.IsRecordingHotkey), raised);
    }

    [Fact]
    public void SetRegionCaptureHotkey_UpdatesDisplayName()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RegionCaptureHotkey = 0x41u; // 'A'

        Assert.Contains(nameof(vm.RegionCaptureHotkeyDisplayName), raised);
        Assert.NotEmpty(vm.RegionCaptureHotkeyDisplayName);
    }

    [Fact]
    public void SetRegionCaptureHotkeyModifiers_UpdatesDisplayName()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RegionCaptureHotkeyModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;

        Assert.Contains(nameof(vm.RegionCaptureHotkeyDisplayName), raised);
        Assert.Contains("Ctrl", vm.RegionCaptureHotkeyDisplayName);
        Assert.Contains("Shift", vm.RegionCaptureHotkeyDisplayName);
    }

    [Fact]
    public void BrowseScreenshotPathCommand_UsesDialogSelection()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock
            .Setup(d => d.PickFolder(@"C:\Shots", "Select screenshot save folder"))
            .Returns(@"D:\Captures");
        var vm = CreateVm(new UserSettings { ScreenshotSavePath = @"C:\Shots" }, dialogMock.Object);

        vm.BrowseScreenshotPathCommand.Execute(null);

        Assert.Equal(@"D:\Captures", vm.ScreenshotSavePath);
        dialogMock.VerifyAll();
    }

    [Fact]
    public void BrowseRecordingPathCommand_WhenCancelled_KeepsExistingPath()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock
            .Setup(d => d.PickFolder(@"C:\Videos", "Select recording output folder"))
            .Returns((string?)null);
        var vm = CreateVm(new UserSettings { RecordingOutputPath = @"C:\Videos" }, dialogMock.Object);

        vm.BrowseRecordingPathCommand.Execute(null);

        Assert.Equal(@"C:\Videos", vm.RecordingOutputPath);
        dialogMock.VerifyAll();
    }

    [Fact]
    public void LoadsFromSettings_RecordMicrophone()
    {
        var vm = CreateVm(new UserSettings { RecordMicrophone = true });

        Assert.True(vm.RecordMicrophone);
    }

    [Fact]
    public void DefaultSettings_RecordMicrophone_IsEnabled()
    {
        var vm = CreateVm();

        Assert.True(vm.RecordMicrophone);
    }

    [Fact]
    public void LoadsFromSettings_RecordingMicrophoneDeviceName()
    {
        var vm = CreateVm(new UserSettings { RecordingMicrophoneDeviceName = "USB Mic" });

        Assert.Equal("USB Mic", vm.SelectedMicrophoneDeviceName);
    }

    [Fact]
    public void Save_PersistsRecordMicrophone()
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());
        vm.RecordMicrophone = true;
        vm.SelectedMicrophoneDeviceName = "USB Mic";

        vm.SaveCommand.Execute(null);

        Assert.True(saved?.RecordMicrophone);
        Assert.Equal("USB Mic", saved?.RecordingMicrophoneDeviceName);
    }

    [Fact]
    public void RecordMicrophone_PropertyChanged_Fired()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RecordMicrophone = false;

        Assert.Contains(nameof(vm.RecordMicrophone), raised);
    }

    [Fact]
    public void SelectedMicrophoneDeviceName_DefaultsToResolvedDevice()
    {
        var vm = CreateVm();

        Assert.Equal("Studio Mic", vm.SelectedMicrophoneDeviceName);
        Assert.Equal(2, vm.AvailableMicrophoneDevices.Count);
        Assert.True(vm.HasAvailableMicrophoneDevices);
    }

    [Fact]
    public void LoadsFromSettings_RecordingCursorEffectSettings()
    {
        var vm = CreateVm(new UserSettings
        {
            RecordingCursorHighlightEnabled = false,
            RecordingClickRippleEnabled = false,
            RecordingCursorHighlightSize = 34d,
        });

        Assert.False(vm.RecordingCursorHighlightEnabled);
        Assert.False(vm.RecordingClickRippleEnabled);
        Assert.Equal(34d, vm.RecordingCursorHighlightSize);
    }

    [Fact]
    public void Save_PersistsRecordingCursorEffectSettings()
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(
            mock.Object,
            Mock.Of<IThemeService>(),
            Mock.Of<IDialogService>(),
            CreateMicrophoneDeviceService());
        vm.RecordingCursorHighlightEnabled = false;
        vm.RecordingClickRippleEnabled = false;
        vm.RecordingCursorHighlightSize = 36d;

        vm.SaveCommand.Execute(null);

        Assert.False(saved?.RecordingCursorHighlightEnabled);
        Assert.False(saved?.RecordingClickRippleEnabled);
        Assert.Equal(36d, saved?.RecordingCursorHighlightSize);
    }

    [Fact]
    public void RecordingCursorEffectSettings_PropertyChanged_Fired()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RecordingCursorHighlightEnabled = false;
        vm.RecordingClickRippleEnabled = false;
        vm.RecordingCursorHighlightSize = 30d;

        Assert.Contains(nameof(vm.RecordingCursorHighlightEnabled), raised);
        Assert.Contains(nameof(vm.RecordingClickRippleEnabled), raised);
        Assert.Contains(nameof(vm.RecordingCursorHighlightSize), raised);
    }

    [Fact]
    public void PickAnnotationColorCommand_UsesDialogSelection()
    {
        var dialogMock = new Mock<IDialogService>();
        dialogMock
            .Setup(d => d.PickColor(Colors.Red))
            .Returns(Colors.DodgerBlue);
        var vm = CreateVm(new UserSettings { DefaultAnnotationColor = "#FFFF0000" }, dialogMock.Object);

        vm.PickAnnotationColorCommand.Execute(null);

        Assert.Equal(Colors.DodgerBlue, vm.DefaultAnnotationColor);
        dialogMock.VerifyAll();
    }

    [Fact]
    public void SelectedSection_DefaultsToCapture()
    {
        var vm = CreateVm();

        Assert.Equal(SettingsSection.Capture, vm.SelectedSection);
        Assert.True(vm.IsCaptureSectionSelected);
        Assert.Equal("Capture", vm.SelectedSectionDisplayName);
    }

    [Fact]
    public void SelectedSection_UpdatesDerivedProperties()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedSection = SettingsSection.App;

        Assert.True(vm.IsAppSectionSelected);
        Assert.Equal("App", vm.SelectedSectionDisplayName);
        Assert.Contains(nameof(vm.SelectedSectionDisplayName), raised);
        Assert.Contains(nameof(vm.IsAppSectionSelected), raised);
    }

    [Fact]
    public void ResetCurrentSectionCommand_WhenRecordingSelected_ResetsRecordingValuesOnly()
    {
        var vm = CreateVm(new UserSettings
        {
            ScreenshotSavePath = @"C:\Shots",
            RecordingOutputPath = @"C:\Videos",
            RecordMicrophone = true,
            GifFps = 20,
            RecordingCursorHighlightEnabled = false,
            RecordingClickRippleEnabled = false,
            RecordingCursorHighlightSize = 36d,
        });

        vm.ScreenshotSavePath = @"D:\Keep";
        vm.RecordingOutputPath = @"D:\Reset";
        vm.RecordMicrophone = true;
        vm.GifFps = 20;
        vm.RecordingCursorHighlightEnabled = false;
        vm.RecordingClickRippleEnabled = false;
        vm.RecordingCursorHighlightSize = 36d;
        vm.SelectedSection = SettingsSection.Recording;

        vm.ResetCurrentSectionCommand.Execute(null);

        var defaults = new UserSettings();
        Assert.Equal(@"D:\Keep", vm.ScreenshotSavePath);
        Assert.Equal(defaults.RecordingOutputPath, vm.RecordingOutputPath);
        Assert.Equal(defaults.RecordMicrophone, vm.RecordMicrophone);
        Assert.Equal(defaults.GifFps, vm.GifFps);
        Assert.Equal(defaults.RecordingCursorHighlightEnabled, vm.RecordingCursorHighlightEnabled);
        Assert.Equal(defaults.RecordingClickRippleEnabled, vm.RecordingClickRippleEnabled);
        Assert.Equal(defaults.RecordingCursorHighlightSize, vm.RecordingCursorHighlightSize);
    }

    [Fact]
    public void RestoreDefaultsCommand_SaveUsesDefaultBaseSettings()
    {
        var current = new UserSettings
        {
            RecordingFps = 7,
            HudGapPixels = 99,
            LastAutoUpdateCheckUtc = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        var defaults = new UserSettings();
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(current);
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());

        vm.RestoreDefaultsCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(defaults.RecordingFps, saved!.RecordingFps);
        Assert.Equal(defaults.HudGapPixels, saved.HudGapPixels);
        Assert.Equal(defaults.LastAutoUpdateCheckUtc, saved.LastAutoUpdateCheckUtc);
    }

    [Fact]
    public void RestoreDefaultsCommand_ThenEditingVisibleSetting_StillPersistsHiddenDefaults()
    {
        var current = new UserSettings
        {
            RecordingFps = 7,
            HudGapPixels = 99,
            LastAutoUpdateCheckUtc = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        var defaults = new UserSettings();
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(current);
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());

        vm.RestoreDefaultsCommand.Execute(null);
        vm.ScreenshotSavePath = @"D:\Screens";
        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(@"D:\Screens", saved!.ScreenshotSavePath);
        Assert.Equal(defaults.RecordingFps, saved.RecordingFps);
        Assert.Equal(defaults.HudGapPixels, saved.HudGapPixels);
        Assert.Equal(defaults.LastAutoUpdateCheckUtc, saved.LastAutoUpdateCheckUtc);
    }

    [Fact]
    public void Save_PreservesActivationTelemetryFields()
    {
        var installCreatedUtc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var current = new UserSettings
        {
            InstallId = "install-123",
            InstallCreatedUtc = installCreatedUtc,
            FirstCaptureCompletedTracked = true,
            FirstRecordingCompletedTracked = true,
        };

        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(current);
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());

        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal(current.InstallId, saved!.InstallId);
        Assert.Equal(current.InstallCreatedUtc, saved.InstallCreatedUtc);
        Assert.Equal(current.FirstCaptureCompletedTracked, saved.FirstCaptureCompletedTracked);
        Assert.Equal(current.FirstRecordingCompletedTracked, saved.FirstRecordingCompletedTracked);
    }

    [Fact]
    public void Sections_ProvideSingleSourceOfTruthForHeaderMetadata()
    {
        var vm = CreateVm();

        var appSection = Assert.Single(vm.Sections, section => section.Section == SettingsSection.App);

        Assert.Equal(appSection.DisplayName, vm.SelectedSectionDisplayName.Replace("Capture", "App"));
        vm.SelectedSection = SettingsSection.App;
        Assert.Equal(appSection.DisplayName, vm.SelectedSectionDisplayName);
        Assert.Equal(appSection.Description, vm.SelectedSectionDescription);
    }

    [Fact]
    public void AnnotationPreviewThickness_HasMinimumOfOne()
    {
        var vm = CreateVm();

        vm.DefaultStrokeThickness = 0.25d;
        Assert.Equal(1d, vm.AnnotationPreviewThickness);

        vm.DefaultStrokeThickness = 3d;
        Assert.Equal(3d, vm.AnnotationPreviewThickness);
    }

    [Fact]
    public void UserSettings_Default_WholeScreenRecordHotkey_IsCtrlShiftR()
    {
        var settings = new UserSettings();

        Assert.Equal(0x52u, settings.WholeScreenRecordHotkey);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, settings.WholeScreenRecordHotkeyModifiers);
    }

    [Fact]
    public void LoadsFromSettings_WholeScreenRecordHotkey()
    {
        var vm = CreateVm(new UserSettings
        {
            WholeScreenRecordHotkey = 0x4Bu, // 'K'
            WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Shift,
        });

        Assert.Equal(0x4Bu, vm.WholeScreenRecordHotkey);
        Assert.Equal(HotkeyModifiers.Shift, vm.WholeScreenRecordHotkeyModifiers);
    }

    [Fact]
    public void Save_PersistsWholeScreenRecordHotkey()
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>(), CreateMicrophoneDeviceService());
        vm.WholeScreenRecordHotkey = 0x4Cu; // 'L'
        vm.WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;

        vm.SaveCommand.Execute(null);

        Assert.Equal(0x4Cu, saved?.WholeScreenRecordHotkey);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, saved?.WholeScreenRecordHotkeyModifiers);
    }

    [Fact]
    public void SetWholeScreenRecordHotkey_UpdatesDisplayName()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.WholeScreenRecordHotkey = 0x4Du; // 'M'

        Assert.Contains(nameof(vm.WholeScreenRecordHotkeyDisplayName), raised);
        Assert.Contains("Ctrl", vm.WholeScreenRecordHotkeyDisplayName);
        Assert.Contains("Shift", vm.WholeScreenRecordHotkeyDisplayName);
        Assert.NotEmpty(vm.WholeScreenRecordHotkeyDisplayName);
    }

    [Fact]
    public void SetWholeScreenRecordHotkeyModifiers_UpdatesDisplayName()
    {
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Shift;

        Assert.Contains(nameof(vm.WholeScreenRecordHotkeyDisplayName), raised);
        Assert.Contains("Shift", vm.WholeScreenRecordHotkeyDisplayName);
        Assert.DoesNotContain("Ctrl", vm.WholeScreenRecordHotkeyDisplayName);
    }

    [Fact]
    public void ResetRecordHotkeyCommand_RestoresDefaultAndCancelsCapture()
    {
        var vm = CreateVm(new UserSettings { WholeScreenRecordHotkey = 0x4Bu, WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Shift });
        vm.StartCapturingWholeScreenRecordHotkeyCommand.Execute(null);
        Assert.True(vm.IsCapturingWholeScreenRecordHotkey);

        vm.ResetRecordHotkeyCommand.Execute(null);

        Assert.Equal(0x52u, vm.WholeScreenRecordHotkey);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, vm.WholeScreenRecordHotkeyModifiers);
        Assert.False(vm.IsCapturingWholeScreenRecordHotkey);
    }

    [Fact]
    public void RegionCaptureHotkeyDisplayName_WhenVkIsZero_ReturnsNotSet()
    {
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0 });

        Assert.Equal("Not set", vm.RegionCaptureHotkeyDisplayName);
    }

    [Fact]
    public void RegionCaptureHotkeyDisplayName_WithAltModifier_IncludesAlt()
    {
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0x41, RegionCaptureHotkeyModifiers = HotkeyModifiers.Alt });

        Assert.Contains("Alt", vm.RegionCaptureHotkeyDisplayName);
    }

    [Fact]
    public void ResetCurrentSectionCommand_CaptureSection_ResetsHotkeyAndModifiers()
    {
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0x41, RegionCaptureHotkeyModifiers = HotkeyModifiers.Ctrl });
        vm.SelectedSection = SettingsSection.Capture;
        vm.StartRecordingHotkeyCommand.Execute(null);

        vm.ResetCurrentSectionCommand.Execute(null);

        Assert.Equal(new UserSettings().RegionCaptureHotkey, vm.RegionCaptureHotkey);
        Assert.Equal(HotkeyModifiers.None, vm.RegionCaptureHotkeyModifiers);
        Assert.False(vm.IsRecordingHotkey);
    }

    [Fact]
    public void ResetCurrentSectionCommand_RecordingSection_ResetsWholeScreenRecordHotkeyAndModifiers()
    {
        var vm = CreateVm(new UserSettings { WholeScreenRecordHotkey = 0x41, WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Alt });
        vm.SelectedSection = SettingsSection.Recording;
        vm.StartCapturingWholeScreenRecordHotkeyCommand.Execute(null);

        vm.ResetCurrentSectionCommand.Execute(null);

        Assert.Equal(new UserSettings().WholeScreenRecordHotkey, vm.WholeScreenRecordHotkey);
        Assert.Equal(new UserSettings().WholeScreenRecordHotkeyModifiers, vm.WholeScreenRecordHotkeyModifiers);
        Assert.False(vm.IsCapturingWholeScreenRecordHotkey);
    }

    [Fact]
    public void ResetCurrentSectionCommand_ShortcutsSection_ResetsOverlayShortcutsAndCaptureState()
    {
        var vm = CreateVm();
        vm.OverlayCopyHotkey = 0x58u;
        vm.OverlayCopyHotkeyModifiers = HotkeyModifiers.Alt;
        vm.OverlaySaveAsHotkey = 0x41u;
        vm.OverlaySaveAsHotkeyModifiers = HotkeyModifiers.Ctrl;
        vm.SelectedSection = SettingsSection.Shortcuts;
        vm.StartCapturingOverlayShortcutCommand.Execute("OverlaySaveAs");

        vm.ResetCurrentSectionCommand.Execute(null);

        var defaults = new UserSettings();
        Assert.Equal(defaults.OverlayCopyHotkey, vm.OverlayCopyHotkey);
        Assert.Equal(defaults.OverlayCopyHotkeyModifiers, vm.OverlayCopyHotkeyModifiers);
        Assert.Equal(defaults.OverlaySaveAsHotkey, vm.OverlaySaveAsHotkey);
        Assert.Equal(defaults.OverlaySaveAsHotkeyModifiers, vm.OverlaySaveAsHotkeyModifiers);
        Assert.False(vm.IsCapturingOverlayShortcut);
        Assert.Equal(string.Empty, vm.OverlayShortcutCaptureTarget);
        Assert.Equal(string.Empty, vm.OverlayShortcutConflictMessage);
    }

    [Fact]
    public void ResetCurrentSectionCommand_CaptureSection_ResetsWatermarkFields()
    {
        var vm = CreateVm();
        vm.SelectedSection = SettingsSection.Capture;
        vm.WatermarkEnabled = true;
        vm.WatermarkTextTemplate = WatermarkTextTemplate.TimezoneOnly;
        vm.WatermarkPosition = WatermarkPosition.TopLeft;
        vm.WatermarkFontSize = 42d;
        vm.WatermarkApplyToCopy = false;
        vm.WatermarkApplyToSave = false;

        vm.ResetCurrentSectionCommand.Execute(null);

        var defaults = new UserSettings().ScreenshotWatermark;
        Assert.Equal(defaults.Enabled, vm.WatermarkEnabled);
        Assert.Equal(defaults.TextTemplate, vm.WatermarkTextTemplate);
        Assert.Equal(defaults.Position, vm.WatermarkPosition);
        Assert.Equal(defaults.FontSize, vm.WatermarkFontSize);
        Assert.Equal(defaults.ApplyToCopy, vm.WatermarkApplyToCopy);
        Assert.Equal(defaults.ApplyToSave, vm.WatermarkApplyToSave);
    }

    [Fact]
    public void RestoreDefaultsCommand_ResetsWatermarkFields()
    {
        var vm = CreateVm();
        vm.WatermarkEnabled = true;
        vm.WatermarkTextTemplate = WatermarkTextTemplate.TimezoneOnly;
        vm.WatermarkPosition = WatermarkPosition.TopLeft;
        vm.WatermarkFontSize = 42d;
        vm.WatermarkApplyToCopy = false;
        vm.WatermarkApplyToSave = false;

        vm.RestoreDefaultsCommand.Execute(null);

        var defaults = new UserSettings().ScreenshotWatermark;
        Assert.Equal(defaults.Enabled, vm.WatermarkEnabled);
        Assert.Equal(defaults.TextTemplate, vm.WatermarkTextTemplate);
        Assert.Equal(defaults.Position, vm.WatermarkPosition);
        Assert.Equal(defaults.FontSize, vm.WatermarkFontSize);
        Assert.Equal(defaults.ApplyToCopy, vm.WatermarkApplyToCopy);
        Assert.Equal(defaults.ApplyToSave, vm.WatermarkApplyToSave);
    }
}
