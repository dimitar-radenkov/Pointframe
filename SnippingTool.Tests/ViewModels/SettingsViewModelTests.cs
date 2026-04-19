using Moq;
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
    }

    [Fact]
    public void LoadsFromSettings_RegionCaptureHotkey()
    {
        // Arrange
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0x41 }); // 'A'

        // Assert
        Assert.Equal(0x41u, vm.RegionCaptureHotkey);
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

        // Act
        vm.SaveCommand.Execute(null);

        // Assert
        Assert.Equal(0x42u, saved?.RegionCaptureHotkey);
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
        var vm = CreateVm(new UserSettings { RegionCaptureHotkey = 0x41 });
        vm.StartRecordingHotkeyCommand.Execute(null);

        vm.ResetHotkeyCommand.Execute(null);

        Assert.Equal(0x2Cu, vm.RegionCaptureHotkey);
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
}
