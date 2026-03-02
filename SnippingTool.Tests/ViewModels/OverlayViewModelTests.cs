using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public class OverlayViewModelTests
{
    private static OverlayViewModel Vm() => new(new AnnotationGeometryService(), NullLogger<OverlayViewModel>.Instance);

    [Fact]
    public void InitialPhase_IsSelecting()
    {
        var vm = Vm();
        Assert.Equal(OverlayViewModel.Phase.Selecting, vm.CurrentPhase);
    }

    [Fact]
    public void InitialSelectionRect_IsEmpty()
    {
        var vm = Vm();
        Assert.Equal(Rect.Empty, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_SetsSelectionRect()
    {
        var vm = Vm();
        var rect = new Rect(10, 20, 300, 200);

        vm.CommitSelection(rect);

        Assert.Equal(rect, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_TransitionsToAnnotating()
    {
        var vm = Vm();

        vm.CommitSelection(new Rect(0, 0, 100, 100));

        Assert.Equal(OverlayViewModel.Phase.Annotating, vm.CurrentPhase);
    }

    [Fact]
    public void UpdateSizeLabel_FormatsWithDpi()
    {
        var vm = Vm();
        vm.DpiX = 2.0;
        vm.DpiY = 2.0;

        vm.UpdateSizeLabel(100, 50);

        Assert.Equal("200×100", vm.SizeLabel);
    }

    [Fact]
    public void UpdateSizeLabel_DefaultDpi_FormatsCorrectly()
    {
        var vm = Vm();

        vm.UpdateSizeLabel(640, 480);

        Assert.Equal("640×480", vm.SizeLabel);
    }

    [Fact]
    public void CopyCommand_FiresCopyRequested()
    {
        var vm = Vm();
        var fired = false;
        vm.CopyRequested += () => fired = true;

        vm.CopyCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested()
    {
        var vm = Vm();
        var fired = false;
        vm.CloseRequested += () => fired = true;

        vm.CloseCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CurrentPhase_PropertyChanged_FiredOnCommit()
    {
        var vm = Vm();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CurrentPhase))
            {
                raised = true;
            }
        };

        vm.CommitSelection(new Rect(0, 0, 50, 50));

        Assert.True(raised);
    }
}
