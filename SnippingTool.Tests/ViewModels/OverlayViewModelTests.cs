using System.Windows;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public class OverlayViewModelTests
{
    [Fact]
    public void InitialPhase_IsSelecting()
    {
        var vm = new OverlayViewModel();
        Assert.Equal(OverlayViewModel.Phase.Selecting, vm.CurrentPhase);
    }

    [Fact]
    public void InitialSelectionRect_IsEmpty()
    {
        var vm = new OverlayViewModel();
        Assert.Equal(Rect.Empty, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_SetsSelectionRect()
    {
        var vm = new OverlayViewModel();
        var rect = new Rect(10, 20, 300, 200);

        vm.CommitSelection(rect);

        Assert.Equal(rect, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_TransitionsToAnnotating()
    {
        var vm = new OverlayViewModel();

        vm.CommitSelection(new Rect(0, 0, 100, 100));

        Assert.Equal(OverlayViewModel.Phase.Annotating, vm.CurrentPhase);
    }

    [Fact]
    public void UpdateSizeLabel_FormatsWithDpi()
    {
        var vm = new OverlayViewModel { DpiX = 2.0, DpiY = 2.0 };

        vm.UpdateSizeLabel(100, 50);

        Assert.Equal("200×100", vm.SizeLabel);
    }

    [Fact]
    public void UpdateSizeLabel_DefaultDpi_FormatsCorrectly()
    {
        var vm = new OverlayViewModel();

        vm.UpdateSizeLabel(640, 480);

        Assert.Equal("640×480", vm.SizeLabel);
    }

    [Fact]
    public void CopyCommand_FiresCopyRequested()
    {
        var vm = new OverlayViewModel();
        var fired = false;
        vm.CopyRequested += () => fired = true;

        vm.CopyCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested()
    {
        var vm = new OverlayViewModel();
        var fired = false;
        vm.CloseRequested += () => fired = true;

        vm.CloseCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CurrentPhase_PropertyChanged_FiredOnCommit()
    {
        var vm = new OverlayViewModel();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CurrentPhase)) raised = true;
        };

        vm.CommitSelection(new Rect(0, 0, 50, 50));

        Assert.True(raised);
    }
}
