using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class RecordingHudViewModelTests
{
    private static Mock<IScreenRecordingService> DefaultSvcMock() => new();

    private static RecordingHudViewModel CreateVm(
        Mock<IScreenRecordingService>? svcMock = null,
        string outputPath = @"C:\Videos\rec.mp4",
        IEventAggregator? eventAggregator = null)
    {
        var svc = (svcMock ?? DefaultSvcMock()).Object;
        return new RecordingHudViewModel(
            svc,
            outputPath,
            eventAggregator ?? Mock.Of<IEventAggregator>(),
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
    public void InitialState_UsesMicrophoneStateFromService()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);
        svcMock.SetupGet(service => service.IsMicrophoneMuted).Returns(false);

        var vm = CreateVm(svcMock: svcMock);

        Assert.True(vm.CanToggleMicrophone);
        Assert.False(vm.IsMicrophoneMuted);
        Assert.Equal("Mute", vm.MicrophoneActionLabel);
    }

    [Fact]
    public void InitialState_WhenMicrophoneUnavailable_ShowsOffLabelAndExplanation()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.SetupGet(service => service.CanToggleMicrophone).Returns(false);
        svcMock.SetupGet(service => service.IsMicrophoneMuted).Returns(false);

        var vm = CreateVm(svcMock: svcMock);

        Assert.False(vm.CanToggleMicrophone);
        Assert.Equal("Mic off", vm.MicrophoneActionLabel);
        Assert.Equal(
            "Microphone controls are unavailable for this recording. Enable Record microphone in Settings and make sure a compatible microphone device is selected.",
            vm.MicrophoneToolTip);
    }

    [Fact]
    public void OutputPath_ExposesConstructorValue()
    {
        var vm = CreateVm(outputPath: @"C:\Videos\test.mp4");

        Assert.Equal(@"C:\Videos\test.mp4", vm.OutputPath);
    }

    [Fact]
    public async Task StopCommand_DisablesPauseResume()
    {
        var vm = CreateVm();

        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.CanPauseResume);
    }

    [Fact]
    public async Task StopCommand_CallsServiceStop()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        var vm = CreateVm(svcMock: svcMock);

        await vm.StopCommand.ExecuteAsync(null);

        svcMock.Verify(service => service.Stop(), Times.Once);
    }

    [Fact]
    public async Task StopCommand_PublishesRecordingCompletedMessage()
    {
        var eventAggregatorMock = new Mock<IEventAggregator>();
        eventAggregatorMock
            .Setup(aggregator => aggregator.Publish(It.IsAny<object>()))
            .Returns(ValueTask.CompletedTask);
        var vm = CreateVm(eventAggregator: eventAggregatorMock.Object);

        await vm.StopCommand.ExecuteAsync(null);

        eventAggregatorMock.Verify(
            aggregator => aggregator.Publish(It.Is<RecordingCompletedMessage>(message =>
                message.OutputPath == @"C:\Videos\rec.mp4"
                && message.ElapsedText == vm.ElapsedText)),
            Times.Once);
    }

    [Fact]
    public async Task StopCommand_FiresCloseRequested()
    {
        var vm = CreateVm();
        var fired = false;
        vm.CloseRequested += () => fired = true;

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
    public void ToggleMicrophoneCommand_WhenAvailable_MutesMicrophone()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);
        svcMock.SetupGet(service => service.IsMicrophoneMuted).Returns(false);
        svcMock.Setup(service => service.TrySetMicrophoneMuted(true)).Returns(true);
        var vm = CreateVm(svcMock: svcMock);

        vm.ToggleMicrophoneCommand.Execute(null);

        svcMock.Verify(service => service.TrySetMicrophoneMuted(true), Times.Once);
        Assert.True(vm.IsMicrophoneMuted);
        Assert.Equal("Unmute", vm.MicrophoneActionLabel);
    }

    [Fact]
    public void ToggleMicrophoneCommand_WhenMuted_UnmutesMicrophone()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);
        svcMock.SetupGet(service => service.IsMicrophoneMuted).Returns(true);
        svcMock.Setup(service => service.TrySetMicrophoneMuted(false)).Returns(true);
        var vm = CreateVm(svcMock: svcMock);

        vm.ToggleMicrophoneCommand.Execute(null);

        svcMock.Verify(service => service.TrySetMicrophoneMuted(false), Times.Once);
        Assert.False(vm.IsMicrophoneMuted);
        Assert.Equal("Mute", vm.MicrophoneActionLabel);
    }

    [Fact]
    public void ToggleMicrophoneCommand_WhenUnavailable_IsNoOp()
    {
        var svcMock = new Mock<IScreenRecordingService>();
        svcMock.SetupGet(service => service.CanToggleMicrophone).Returns(false);
        var vm = CreateVm(svcMock: svcMock);

        vm.ToggleMicrophoneCommand.Execute(null);

        svcMock.Verify(service => service.TrySetMicrophoneMuted(It.IsAny<bool>()), Times.Never);
        Assert.False(vm.IsMicrophoneMuted);
        Assert.Equal("Mic off", vm.MicrophoneActionLabel);
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
    public void SelectToolCommand_UpdatesAnnotationViewModelTool_ForNumber()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();
        vm.AttachAnnotationSession(annotationViewModel, () => false);

        vm.SelectToolCommand.Execute(nameof(AnnotationTool.Number));

        Assert.Equal(AnnotationTool.Number, annotationViewModel.SelectedTool);
    }

    [Fact]
    public void ToggleAnnotationInputCommand_BeforeSessionAttached_IsNoOp()
    {
        var vm = CreateVm();

        vm.ToggleAnnotationInputCommand.Execute(null);

        Assert.False(vm.IsAnnotationInputArmed);
        Assert.False(vm.IsAnnotationPanelVisible);
        Assert.Equal("Interactive", vm.CurrentModeLabel);
        Assert.Equal("Annotate", vm.AnnotationModeLabel);
    }

    [Fact]
    public void SelectToolCommand_WithUnknownToolName_IsNoOp()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();
        vm.AttachAnnotationSession(annotationViewModel, () => false);

        vm.SelectToolCommand.Execute("NotATool");

        Assert.Equal(AnnotationTool.Pen, annotationViewModel.SelectedTool);
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
    public void ToggleAnnotationInputCommand_RaisesModePropertiesAcrossStateTransitions()
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
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ToggleAnnotationInputCommand.Execute(null);
        vm.ToggleAnnotationInputCommand.Execute(null);

        Assert.Contains(nameof(vm.IsAnnotationInputArmed), changed);
        Assert.Contains(nameof(vm.IsAnnotationPanelVisible), changed);
        Assert.Contains(nameof(vm.CurrentModeLabel), changed);
        Assert.Contains(nameof(vm.AnnotationModeLabel), changed);
    }

    [Fact]
    public void UndoAnnotationsCommand_ExecutesUndoOnAnnotationViewModel()
    {
        var aggregatorMock = new Mock<IEventAggregator>();
        aggregatorMock.Setup(aggregator => aggregator.Publish(It.IsAny<object>())).Returns(ValueTask.CompletedTask);
        var annotationViewModel = CreateAnnotationViewModel(aggregatorMock);
        annotationViewModel.BeginGroup();
        annotationViewModel.TrackElement(new object());
        annotationViewModel.CommitGroup();

        var vm = CreateVm();
        vm.AttachAnnotationSession(annotationViewModel, () => false);

        vm.UndoAnnotationsCommand.Execute(null);

        Assert.Equal(0, annotationViewModel.UndoCount);
        Assert.Equal(1, annotationViewModel.RedoCount);
        aggregatorMock.Verify(aggregator => aggregator.Publish(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public void UndoAnnotationsCommand_WithoutAnnotationSession_IsNoOp()
    {
        var vm = CreateVm();

        vm.UndoAnnotationsCommand.Execute(null);

        Assert.False(vm.CanManageAnnotations);
    }

    [Fact]
    public void ClearAnnotationsCommand_ClearsAttachedAnnotationSession()
    {
        var vm = CreateVm();
        var annotationViewModel = CreateAnnotationViewModel();
        annotationViewModel.BeginGroup();
        annotationViewModel.TrackElement(new object());
        annotationViewModel.CommitGroup();
        annotationViewModel.MarkAnnotationCommitted();
        vm.AttachAnnotationSession(annotationViewModel, () => false);

        vm.ClearAnnotationsCommand.Execute(null);

        Assert.False(annotationViewModel.HasActiveAnnotations);
        Assert.Equal(0, annotationViewModel.UndoCount);
        Assert.Equal(0, annotationViewModel.RedoCount);
    }

    [Fact]
    public void ClearAnnotationsCommand_WithoutAnnotationSession_IsNoOp()
    {
        var vm = CreateVm();

        vm.ClearAnnotationsCommand.Execute(null);

        Assert.False(vm.CanManageAnnotations);
    }
}
