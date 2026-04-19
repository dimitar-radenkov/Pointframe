using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests.ViewModels;

public sealed class RecordingAnnotationViewModelTests
{
    private static RecordingAnnotationViewModel CreateViewModel(UserSettings? settings = null)
    {
        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(settings ?? new UserSettings());

        return new RecordingAnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<RecordingAnnotationViewModel>.Instance,
            settingsMock.Object,
            Mock.Of<IEventAggregator>());
    }

    [Fact]
    public void Constructor_DefaultsSelectedToolToPen()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(AnnotationTool.Pen, viewModel.SelectedTool);
    }

    [Fact]
    public void SetInputArmed_UpdatesState()
    {
        var viewModel = CreateViewModel();

        var result = viewModel.SetInputArmed(true);

        Assert.True(result);
        Assert.True(viewModel.IsInputArmed);
    }

    [Fact]
    public void ClearCommand_RaisesEventAndResetsAnnotationState()
    {
        var viewModel = CreateViewModel();
        var clearRequested = false;
        viewModel.ClearRequested += () => clearRequested = true;
        viewModel.BeginGroup();
        viewModel.TrackElement(new object());
        viewModel.CommitGroup();
        viewModel.MarkAnnotationCommitted();
        viewModel.ResetNumberCounter(4);

        viewModel.ClearCommand.Execute(null);

        Assert.True(clearRequested);
        Assert.False(viewModel.HasActiveAnnotations);
        Assert.Equal(0, viewModel.NumberCounter);
        Assert.Equal(0, viewModel.UndoCount);
        Assert.Equal(0, viewModel.RedoCount);
    }

    [Fact]
    public void SyncAnnotationState_UpdatesFlagsAndCounter()
    {
        var viewModel = CreateViewModel();

        viewModel.SyncAnnotationState(true, 3);

        Assert.True(viewModel.HasActiveAnnotations);
        Assert.Equal(3, viewModel.NumberCounter);
    }
}