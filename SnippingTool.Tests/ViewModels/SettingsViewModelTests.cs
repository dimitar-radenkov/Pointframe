using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private static SettingsViewModel CreateVm(UserSettings? settings = null, IDialogService? dialogService = null)
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(settings ?? new UserSettings());
        return new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), dialogService ?? Mock.Of<IDialogService>());
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
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>());
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
    public void UserSettings_Default_RecordingFormat_IsMp4()
    {
        // Arrange
        var settings = new UserSettings();

        // Assert
        Assert.Equal(RecordingFormat.Mp4, settings.RecordingFormat);
    }

    [Fact]
    public void LoadsFromSettings_RecordingFormat()
    {
        // Arrange
        var vm = CreateVm(new UserSettings { RecordingFormat = RecordingFormat.Avi });

        // Assert
        Assert.Equal(RecordingFormat.Avi, vm.RecordingFormat);
    }

    [Fact]
    public void Save_PersistsRecordingFormat()
    {
        // Arrange
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(new UserSettings());
        UserSettings? saved = null;
        mock.Setup(s => s.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(s => saved = s);
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>());
        vm.RecordingFormat = RecordingFormat.Avi;

        // Act
        vm.SaveCommand.Execute(null);

        // Assert
        Assert.Equal(RecordingFormat.Avi, saved?.RecordingFormat);
    }

    [Fact]
    public void RecordingFormat_PropertyChanged_Fired()
    {
        // Arrange
        var vm = CreateVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.RecordingFormat = RecordingFormat.Avi;

        // Assert
        Assert.Contains(nameof(vm.RecordingFormat), raised);
    }

    [Fact]
    public void RecordingFormatValues_All_ContainsBothFormats()
    {
        // Assert
        Assert.Contains(RecordingFormat.Mp4, RecordingFormatValues.All);
        Assert.Contains(RecordingFormat.Avi, RecordingFormatValues.All);
        Assert.Equal(2, RecordingFormatValues.All.Length);
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
        var vm = new SettingsViewModel(mock.Object, Mock.Of<IThemeService>(), Mock.Of<IDialogService>());
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
}
