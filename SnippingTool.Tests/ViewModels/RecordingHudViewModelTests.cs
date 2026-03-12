using System.Diagnostics;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class RecordingHudViewModelTests
{
    private static Mock<IScreenRecordingService> DefaultSvcMock() => new();

    private static RecordingHudViewModel CreateVm(
        Mock<IScreenRecordingService>? svcMock = null,
        string outputPath = @"C:\Videos\rec.mp4",
        UserSettings? settings = null)
    {
        var svc = (svcMock ?? DefaultSvcMock()).Object;
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.Setup(s => s.Current).Returns(settings ?? new UserSettings());
        var fakeProcess = new Mock<IProcessService>().Object;
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordingHudViewModel>();
        return new RecordingHudViewModel(svc, outputPath, settingsMock.Object, fakeProcess, logger);
    }

    [Fact]
    public void InitialState_ElapsedText_IsZeroed()
    {
        var vm = CreateVm();

        Assert.Equal("00:00", vm.ElapsedText);
    }

    [Fact]
    public void InitialState_IsStopped_IsFalse()
    {
        var vm = CreateVm();

        Assert.False(vm.IsStopped);
    }

    [Fact]
    public void InitialState_PauseResumeLabel_IsPause()
    {
        var vm = CreateVm();

        Assert.Equal("⏸ Pause", vm.PauseResumeLabel);
    }

    [Fact]
    public void InitialState_CanPauseResume_IsTrue()
    {
        var vm = CreateVm();

        Assert.True(vm.CanPauseResume);
    }

    [Fact]
    public void OutputPath_ExposesConstructorValue()
    {
        var vm = CreateVm(outputPath: @"C:\Videos\test.avi");

        Assert.Equal(@"C:\Videos\test.avi", vm.OutputPath);
    }

    [Fact]
    public async Task StopCommand_SetsStopped()
    {
        var vm = CreateVm(settings: new UserSettings { HudCloseDelaySeconds = 0 });

        await vm.StopCommand.ExecuteAsync(null);

        Assert.True(vm.IsStopped);
    }

    [Fact]
    public async Task StopCommand_SetsSavedFileName_ContainsBaseName()
    {
        var vm = CreateVm(outputPath: @"C:\Videos\rec.mp4", settings: new UserSettings { HudCloseDelaySeconds = 0 });

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Contains("rec.mp4", vm.SavedFileName);
    }

    [Fact]
    public async Task StopCommand_DisablesPauseResume()
    {
        var vm = CreateVm(settings: new UserSettings { HudCloseDelaySeconds = 0 });

        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.CanPauseResume);
    }

    [Fact]
    public async Task StopCommand_CallsServiceStop()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        var vm = CreateVm(svcMock: svcMock, settings: new UserSettings { HudCloseDelaySeconds = 0 });

        await vm.StopCommand.ExecuteAsync(null);

        svcMock.Verify(s => s.Stop(), Times.Once);
    }

    [Fact]
    public async Task StopCommand_FiresStopCompleted()
    {
        var vm = CreateVm(settings: new UserSettings { HudCloseDelaySeconds = 0 });
        var fired = false;
        vm.StopCompleted += () => fired = true;

        await vm.StopCommand.ExecuteAsync(null);

        Assert.True(fired);
    }

    [Fact]
    public void PauseResumeCommand_WhenNotPaused_CallsPause()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        var vm = CreateVm(svcMock: svcMock);

        vm.PauseResumeCommand.Execute(null);

        svcMock.Verify(s => s.Pause(), Times.Once);
    }

    [Fact]
    public void PauseResumeCommand_WhenNotPaused_UpdatesLabel()
    {
        var vm = CreateVm();

        vm.PauseResumeCommand.Execute(null);

        Assert.Equal("▶ Resume", vm.PauseResumeLabel);
    }

    [Fact]
    public void PauseResumeCommand_WhenPaused_CallsResume()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.Setup(s => s.IsPaused).Returns(true);
        var vm = CreateVm(svcMock: svcMock);

        vm.PauseResumeCommand.Execute(null);

        svcMock.Verify(s => s.Resume(), Times.Once);
    }

    [Fact]
    public void PauseResumeCommand_WhenPaused_UpdatesLabel()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.Setup(s => s.IsPaused).Returns(true);
        var vm = CreateVm(svcMock: svcMock);

        vm.PauseResumeCommand.Execute(null);

        Assert.Equal("⏸ Pause", vm.PauseResumeLabel);
    }

    [Fact]
    public void PauseResumeCommand_Fires_PropertyChanged_ForLabel()
    {
        var vm = CreateVm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PauseResumeCommand.Execute(null);

        Assert.Contains(nameof(vm.PauseResumeLabel), changed);
    }

    [Fact]
    public async Task StopCommand_Fires_PropertyChanged_ForIsStopped()
    {
        var vm = CreateVm(settings: new UserSettings { HudCloseDelaySeconds = 0 });
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Contains(nameof(vm.IsStopped), changed);
    }

    [Fact]
    public void OpenOutputFolderCommand_InvokesProcess()
    {
        var processMock = new Mock<IProcessService>();
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<RecordingHudViewModel>();
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.Setup(s => s.Current).Returns(new UserSettings());
        var vm = new RecordingHudViewModel(
            new Mock<IScreenRecordingService>().Object,
            @"C:\Videos\rec.mp4",
            settingsMock.Object,
            processMock.Object,
            logger);

        vm.OpenOutputFolderCommand.Execute(null);

        processMock.Verify(
            p => p.Start(It.Is<ProcessStartInfo>(i => i.FileName == "explorer.exe" && i.Arguments == @"C:\Videos")),
            Times.Once);
    }

}
