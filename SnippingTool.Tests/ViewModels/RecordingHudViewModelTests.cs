using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class RecordingHudViewModelTests
{
    private static Mock<IScreenRecordingService> DefaultSvcMock() => new();

    private static RecordingHudViewModel CreateVm(
        Mock<IScreenRecordingService>? svcMock = null,
        string outputPath = @"C:\Videos\rec.mp4",
        UserSettings? settings = null,
        IProcessService? processService = null)
    {
        var svc = (svcMock ?? DefaultSvcMock()).Object;
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.Setup(service => service.Current).Returns(settings ?? new UserSettings());
        return new RecordingHudViewModel(
            svc,
            outputPath,
            settingsMock.Object,
            processService ?? Mock.Of<IProcessService>(),
            NullLogger<RecordingHudViewModel>.Instance);
    }

    private static RecordingAnnotationViewModel CreateAnnotationViewModel(Mock<IEventAggregator>? eventAggregatorMock = null)
    {
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(new UserSettings());
        var aggregator = eventAggregatorMock?.Object ?? Mock.Of<IEventAggregator>();

        return new RecordingAnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<RecordingAnnotationViewModel>.Instance,
            settingsMock.Object,
            aggregator);
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

        svcMock.Verify(service => service.Stop(), Times.Once);
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

        svcMock.Verify(service => service.Pause(), Times.Once);
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
        svcMock.Setup(service => service.IsPaused).Returns(true);
        var vm = CreateVm(svcMock: svcMock);

        vm.PauseResumeCommand.Execute(null);

        svcMock.Verify(service => service.Resume(), Times.Once);
    }

    [Fact]
    public void PauseResumeCommand_WhenPaused_UpdatesLabel()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.Setup(service => service.IsPaused).Returns(true);
        var vm = CreateVm(svcMock: svcMock);

        vm.PauseResumeCommand.Execute(null);

        Assert.Equal("⏸ Pause", vm.PauseResumeLabel);
    }

    [Fact]
    public void PauseResumeCommand_FiresPropertyChangedForLabel()
    {
        var vm = CreateVm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PauseResumeCommand.Execute(null);

        Assert.Contains(nameof(vm.PauseResumeLabel), changed);
    }

    [Fact]
    public async Task StopCommand_FiresPropertyChangedForIsStopped()
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
        var vm = CreateVm(processService: processMock.Object);

        vm.OpenOutputFolderCommand.Execute(null);

        processMock.Verify(
            process => process.Start(It.Is<ProcessStartInfo>(info => info.FileName == "explorer.exe" && info.Arguments == @"C:\Videos")),
            Times.Once);
    }

    [Fact]
    public void SelectToolCommand_UpdatesAnnotationViewModelTool()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();
        vm.AttachAnnotationSession(annotationViewModel, () => false);

        vm.SelectToolCommand.Execute(nameof(AnnotationTool.Blur));

        Assert.Equal(AnnotationTool.Blur, annotationViewModel.SelectedTool);
        Assert.True(vm.CanManageAnnotations);
    }

    [Fact]
    public void AttachAnnotationSession_LeavesAnnotationPanelHiddenUntilArmed()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();

        vm.AttachAnnotationSession(annotationViewModel, () => false);

        Assert.True(vm.CanManageAnnotations);
        Assert.False(vm.IsAnnotationInputArmed);
        Assert.False(vm.IsAnnotationPanelVisible);
        Assert.Equal("Interactive", vm.CurrentModeLabel);
    }

    [Fact]
    public void ToggleAnnotationInputCommand_ShowsAnnotationPanelWhenArmed()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();
        vm.AttachAnnotationSession(annotationViewModel, () => true);

        vm.ToggleAnnotationInputCommand.Execute(null);

        Assert.True(vm.IsAnnotationInputArmed);
        Assert.True(vm.IsAnnotationPanelVisible);
        Assert.Equal("Drawing", vm.CurrentModeLabel);
        Assert.Equal("Interact", vm.AnnotationModeLabel);
    }

    [Fact]
    public void ToggleAnnotationInputCommand_HidesAnnotationPanelWhenDisarmed()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();
        var nextState = true;
        vm.AttachAnnotationSession(annotationViewModel, () =>
        {
            var currentState = nextState;
            nextState = false;
            return currentState;
        });

        vm.ToggleAnnotationInputCommand.Execute(null);
        vm.ToggleAnnotationInputCommand.Execute(null);

        Assert.False(vm.IsAnnotationInputArmed);
        Assert.False(vm.IsAnnotationPanelVisible);
        Assert.Equal("Interactive", vm.CurrentModeLabel);
        Assert.Equal("Annotate", vm.AnnotationModeLabel);
    }

    [Fact]
    public void UndoAnnotationsCommand_ExecutesUndoOnAnnotationViewModel()
    {
        var aggregatorMock = new Mock<IEventAggregator>();
        aggregatorMock.Setup(aggregator => aggregator.PublishAsync(It.IsAny<object>())).Returns(ValueTask.CompletedTask);
        var annotationViewModel = CreateAnnotationViewModel(aggregatorMock);
        annotationViewModel.BeginGroup();
        annotationViewModel.TrackElement(new object());
        annotationViewModel.CommitGroup();

        var vm = CreateVm();
        vm.AttachAnnotationSession(annotationViewModel, () => false);

        vm.UndoAnnotationsCommand.Execute(null);

        Assert.Equal(0, annotationViewModel.UndoCount);
        Assert.Equal(1, annotationViewModel.RedoCount);
        aggregatorMock.Verify(aggregator => aggregator.PublishAsync(It.IsAny<object>()), Times.Once);
    }
}
