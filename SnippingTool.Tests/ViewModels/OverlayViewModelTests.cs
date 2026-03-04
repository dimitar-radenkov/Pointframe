using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using SnippingTool.Services;
using SnippingTool.Tests;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class OverlayViewModelTests
{
    private static OverlayViewModel Vm() => new(new AnnotationGeometryService(), NullLogger<OverlayViewModel>.Instance, new FakeUserSettingsService());

    [Fact]
    public void InitialPhase_IsSelecting()
    {
        // Arrange
        var vm = Vm();

        // Assert
        Assert.Equal(OverlayViewModel.Phase.Selecting, vm.CurrentPhase);
    }

    [Fact]
    public void InitialSelectionRect_IsEmpty()
    {
        // Arrange
        var vm = Vm();

        // Assert
        Assert.Equal(Rect.Empty, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_SetsSelectionRect()
    {
        // Arrange
        var vm = Vm();
        var rect = new Rect(10, 20, 300, 200);

        // Act
        vm.CommitSelection(rect);

        // Assert
        Assert.Equal(rect, vm.SelectionRect);
    }

    [Fact]
    public void CommitSelection_TransitionsToAnnotating()
    {
        // Arrange
        var vm = Vm();

        // Act
        vm.CommitSelection(new Rect(0, 0, 100, 100));

        // Assert
        Assert.Equal(OverlayViewModel.Phase.Annotating, vm.CurrentPhase);
    }

    [Fact]
    public void UpdateSizeLabel_FormatsWithDpi()
    {
        // Arrange
        var vm = Vm();
        vm.DpiX = 2.0;
        vm.DpiY = 2.0;

        // Act
        vm.UpdateSizeLabel(100, 50);

        // Assert
        Assert.Equal("200×100", vm.SizeLabel);
    }

    [Fact]
    public void UpdateSizeLabel_DefaultDpi_FormatsCorrectly()
    {
        // Arrange
        var vm = Vm();

        // Act
        vm.UpdateSizeLabel(640, 480);

        // Assert
        Assert.Equal("640×480", vm.SizeLabel);
    }

    [Fact]
    public void CopyCommand_FiresCopyRequested()
    {
        // Arrange
        var vm = Vm();
        var fired = false;
        vm.CopyRequested += () => fired = true;

        // Act
        vm.CopyCommand.Execute(null);

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested()
    {
        // Arrange
        var vm = Vm();
        var fired = false;
        vm.CloseRequested += () => fired = true;

        // Act
        vm.CloseCommand.Execute(null);

        // Assert
        Assert.True(fired);
    }

    [Fact]
    public void CurrentPhase_PropertyChanged_FiredOnCommit()
    {
        // Arrange
        var vm = Vm();
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CurrentPhase))
            {
                raised = true;
            }
        };

        // Act
        vm.CommitSelection(new Rect(0, 0, 50, 50));

        // Assert
        Assert.True(raised);
    }
}
