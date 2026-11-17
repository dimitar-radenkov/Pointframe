using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class RecordingHudViewModelCameraTests
{
    private sealed class FakeCameraControl : IRecordingCameraControl
    {
        public bool IsOn { get; set; } = true;

        public int SetOnCalls { get; private set; }

        public List<bool> Requests { get; } = [];

        public event Action? StateChanged;

        public Task SetOnAsync(bool on)
        {
            SetOnCalls++;
            Requests.Add(on);
            IsOn = on;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public void RaiseStateChanged() => StateChanged?.Invoke();
    }

    private static RecordingHudViewModel CreateVm()
    {
        return new RecordingHudViewModel(
            Mock.Of<IScreenRecordingService>(),
            @"C:\Videos\rec.mp4",
            Mock.Of<IEventAggregator>(),
            NullLogger<RecordingHudViewModel>.Instance);
    }

    [Fact]
    public void WithoutACameraSession_TheCameraButtonIsHidden()
    {
        var vm = CreateVm();

        Assert.False(vm.CanToggleCamera);
        Assert.False(vm.IsCameraOn);
    }

    [Fact]
    public void AttachCameraControl_ShowsTheButtonWithTheCurrentState()
    {
        var vm = CreateVm();

        vm.AttachCameraControl(new FakeCameraControl { IsOn = true });

        Assert.True(vm.CanToggleCamera);
        Assert.True(vm.IsCameraOn);
        Assert.Equal("Camera on", vm.CameraActionLabel);
    }

    [Fact]
    public async Task ToggleCamera_WhenOn_TurnsTheCameraOffAndWhenOffTurnsItOnAgain()
    {
        var vm = CreateVm();
        var control = new FakeCameraControl { IsOn = true };
        vm.AttachCameraControl(control);

        await vm.ToggleCameraCommand.ExecuteAsync(null);
        Assert.False(vm.IsCameraOn);
        Assert.Equal("Camera off", vm.CameraActionLabel);

        await vm.ToggleCameraCommand.ExecuteAsync(null);
        Assert.True(vm.IsCameraOn);

        Assert.Equal([false, true], control.Requests);
    }

    [Fact]
    public void AStateChangeFromTheSession_UpdatesTheButtonState()
    {
        var vm = CreateVm();
        var control = new FakeCameraControl { IsOn = true };
        vm.AttachCameraControl(control);

        control.IsOn = false;
        control.RaiseStateChanged();

        Assert.False(vm.IsCameraOn);
    }

    [Fact]
    public async Task ToggleCamera_WithoutAControl_DoesNothing()
    {
        var vm = CreateVm();

        await vm.ToggleCameraCommand.ExecuteAsync(null);

        Assert.False(vm.IsCameraOn);
    }
}
