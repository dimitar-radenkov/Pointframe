using Moq;
using Pointframe.Services;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class SettingsViewModelCameraTests
{
    private static (SettingsViewModel Vm, Mock<IUserSettingsService> Settings, Func<UserSettings?> Saved) Create(
        UserSettings? current = null,
        IReadOnlyList<CameraDevice>? cameras = null)
    {
        var settings = new Mock<IUserSettingsService>();
        settings.SetupGet(service => service.Current).Returns(current ?? new UserSettings());
        UserSettings? saved = null;
        settings.Setup(service => service.Save(It.IsAny<UserSettings>())).Callback<UserSettings>(value => saved = value);
        var microphone = Mock.Of<IMicrophoneDeviceService>(service =>
            service.GetAvailableCaptureDeviceNames() == new[] { "Mic" } &&
            service.GetDefaultCaptureDeviceName() == "Mic");
        var cameraService = new Mock<ICameraDeviceService>();
        cameraService.Setup(service => service.GetCamerasAsync()).ReturnsAsync(cameras ?? []);

        var vm = new SettingsViewModel(
            settings.Object,
            Mock.Of<IThemeService>(),
            Mock.Of<IDialogService>(),
            microphone,
            Mock.Of<ITelemetryService>(),
            Mock.Of<ITranscriptModelService>(),
            null,
            null,
            cameraService.Object);
        return (vm, settings, () => saved);
    }

    [Fact]
    public void Defaults_AreOffNoDeviceSmallBottomRight()
    {
        var defaults = new UserSettings();
        var (vm, _, _) = Create();

        Assert.False(defaults.RecordCamera);
        Assert.Null(defaults.CameraDeviceId);
        Assert.Equal(CameraBubbleSize.Small, defaults.CameraBubbleSize);
        Assert.Equal(CameraBubbleCorner.BottomRight, defaults.CameraBubbleCorner);
        Assert.False(vm.RecordCamera);
        Assert.Equal(string.Empty, vm.SelectedCameraDeviceId);
        Assert.Equal(CameraBubbleSize.Small, vm.CameraBubbleSize);
        Assert.Equal(CameraBubbleCorner.BottomRight, vm.CameraBubbleCorner);
    }

    [Fact]
    public void Save_PersistsTheCameraSettings_AndTheSystemDefaultIsStoredAsNull()
    {
        var (vm, _, saved) = Create();

        vm.SaveCommand.Execute(null);
        Assert.False(saved()!.RecordCamera);
        Assert.Null(saved()!.CameraDeviceId);

        vm.RecordCamera = true;
        vm.SelectedCameraDeviceId = "cam-9";
        vm.CameraBubbleSize = CameraBubbleSize.Large;
        vm.CameraBubbleCorner = CameraBubbleCorner.TopRight;
        vm.SaveCommand.Execute(null);

        Assert.True(saved()!.RecordCamera);
        Assert.Equal("cam-9", saved()!.CameraDeviceId);
        Assert.Equal(CameraBubbleSize.Large, saved()!.CameraBubbleSize);
        Assert.Equal(CameraBubbleCorner.TopRight, saved()!.CameraBubbleCorner);
    }

    [Fact]
    public void LoadsTheSavedCameraSettings()
    {
        var (vm, _, _) = Create(new UserSettings
        {
            RecordCamera = true,
            CameraDeviceId = "cam-3",
            CameraBubbleSize = CameraBubbleSize.Large,
            CameraBubbleCorner = CameraBubbleCorner.BottomLeft,
        });

        Assert.True(vm.RecordCamera);
        Assert.Equal("cam-3", vm.SelectedCameraDeviceId);
        Assert.Equal(CameraBubbleSize.Large, vm.CameraBubbleSize);
        Assert.Equal(CameraBubbleCorner.BottomLeft, vm.CameraBubbleCorner);
    }

    [Fact]
    public void RestoreDefaults_ResetsTheCameraSettings()
    {
        var (vm, _, saved) = Create(new UserSettings
        {
            RecordCamera = true,
            CameraDeviceId = "cam-3",
            CameraBubbleSize = CameraBubbleSize.Large,
            CameraBubbleCorner = CameraBubbleCorner.TopLeft,
        });

        vm.RestoreDefaultsCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.False(saved()!.RecordCamera);
        Assert.Null(saved()!.CameraDeviceId);
        Assert.Equal(CameraBubbleSize.Small, saved()!.CameraBubbleSize);
        Assert.Equal(CameraBubbleCorner.BottomRight, saved()!.CameraBubbleCorner);
    }

    [Fact]
    public void ThePicker_IsHiddenForOneCameraAndShownForSeveral()
    {
        var one = Create(cameras: [new CameraDevice("a", "Cam A")]);
        var two = Create(cameras: [new CameraDevice("a", "Cam A"), new CameraDevice("b", "Cam B")]);

        Assert.False(one.Vm.ShowCameraPicker);
        Assert.True(two.Vm.ShowCameraPicker);
        Assert.Equal(["System default camera", "Cam A", "Cam B"], two.Vm.AvailableCameras.Select(option => option.Name));
    }

    [Fact]
    public void ThePicker_IsShownForASingleCameraWhenTheSavedOneIsMissing()
    {
        var missing = Create(new UserSettings { CameraDeviceId = "gone" }, [new CameraDevice("a", "Cam A")]);
        var connected = Create(new UserSettings { CameraDeviceId = "a" }, [new CameraDevice("a", "Cam A")]);

        Assert.True(missing.Vm.ShowCameraPicker);
        Assert.False(connected.Vm.ShowCameraPicker);
    }

    [Fact]
    public void TheSizeAndCornerChoices_HaveFriendlyLabels()
    {
        var (vm, _, _) = Create();

        Assert.Equal(["Small", "Large"], vm.CameraBubbleSizes.Select(choice => choice.Label));
        Assert.Contains(vm.CameraBubbleCorners, choice => choice.Value == CameraBubbleCorner.TopLeft && choice.Label == "Top left");
    }

    [Fact]
    public void ASavedCameraThatIsNotConnected_StaysSelectedAndIsSavedUnchanged()
    {
        var (vm, _, saved) = Create(
            new UserSettings { RecordCamera = true, CameraDeviceId = "gone" },
            [new CameraDevice("a", "Cam A"), new CameraDevice("b", "Cam B")]);

        vm.SaveCommand.Execute(null);

        Assert.Equal("gone", saved()!.CameraDeviceId);
        Assert.Contains(vm.AvailableCameras, option => option.Id == "gone" && option.Name.Contains("not connected"));
    }

    [Fact]
    public void ASavedCameraThatIsConnected_GetsItsRealName()
    {
        var (vm, _, _) = Create(
            new UserSettings { RecordCamera = true, CameraDeviceId = "b" },
            [new CameraDevice("a", "Cam A"), new CameraDevice("b", "Cam B")]);

        Assert.Equal(3, vm.AvailableCameras.Count);
        Assert.Equal("Cam B", vm.AvailableCameras.Single(option => option.Id == "b").Name);
    }

    [Fact]
    public void ANullSelectionFromAComboBoxRefresh_DoesNotLoseTheSavedCamera()
    {
        var (vm, _, saved) = Create(new UserSettings { RecordCamera = true, CameraDeviceId = "cam-3" });

        vm.SelectedCameraDeviceId = null!;
        vm.SaveCommand.Execute(null);

        Assert.Equal("cam-3", saved()!.CameraDeviceId);
    }
}
