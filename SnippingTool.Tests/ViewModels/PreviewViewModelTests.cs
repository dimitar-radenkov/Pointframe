using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public class PreviewViewModelTests
{
    [Fact]
    public void UndoCommand_CannotExecute_WhenStackEmpty()
    {
        var vm = new PreviewViewModel();
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void RedoCommand_CannotExecute_WhenStackEmpty()
    {
        var vm = new PreviewViewModel();
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void CommitGroup_WithElements_EnablesUndo()
    {
        var vm = new PreviewViewModel();
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();

        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void CommitGroup_Empty_DoesNotEnableUndo()
    {
        var vm = new PreviewViewModel();
        vm.BeginGroup();
        vm.CommitGroup();

        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Undo_MovesGroupToRedoStack()
    {
        var vm = new PreviewViewModel();
        var element = new object();
        vm.BeginGroup();
        vm.TrackElement(element);
        vm.CommitGroup();

        vm.UndoCommand.Execute(null);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void Undo_FiresUndoApplied_WithCorrectGroup()
    {
        var vm = new PreviewViewModel();
        var element = new object();
        vm.BeginGroup();
        vm.TrackElement(element);
        vm.CommitGroup();

        List<object>? received = null;
        vm.UndoApplied += g => received = g;
        vm.UndoCommand.Execute(null);

        Assert.NotNull(received);
        Assert.Contains(element, received);
    }

    [Fact]
    public void Redo_MovesGroupBackToUndoStack()
    {
        var vm = new PreviewViewModel();
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();
        vm.UndoCommand.Execute(null);

        vm.RedoCommand.Execute(null);

        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void Redo_FiresRedoApplied_WithCorrectGroup()
    {
        var vm = new PreviewViewModel();
        var element = new object();
        vm.BeginGroup();
        vm.TrackElement(element);
        vm.CommitGroup();
        vm.UndoCommand.Execute(null);

        List<object>? received = null;
        vm.RedoApplied += g => received = g;
        vm.RedoCommand.Execute(null);

        Assert.NotNull(received);
        Assert.Contains(element, received);
    }

    [Fact]
    public void CommitGroup_ClearsRedoStack()
    {
        var vm = new PreviewViewModel();
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();
        vm.UndoCommand.Execute(null);

        // new stroke after undo — redo must be cleared
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();

        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void UndoCount_TracksStackDepth()
    {
        var vm = new PreviewViewModel();

        vm.BeginGroup(); vm.TrackElement(new object()); vm.CommitGroup();
        Assert.Equal(1, vm.UndoCount);

        vm.BeginGroup(); vm.TrackElement(new object()); vm.CommitGroup();
        Assert.Equal(2, vm.UndoCount);

        vm.UndoCommand.Execute(null);
        Assert.Equal(1, vm.UndoCount);
    }

    [Fact]
    public void CopyCommand_FiresCopyRequested()
    {
        var vm = new PreviewViewModel();
        var fired = false;
        vm.CopyRequested += () => fired = true;

        vm.CopyCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void SaveCommand_FiresSaveRequested()
    {
        var vm = new PreviewViewModel();
        var fired = false;
        vm.SaveRequested += () => fired = true;

        vm.SaveCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void CloseCommand_FiresCloseRequested()
    {
        var vm = new PreviewViewModel();
        var fired = false;
        vm.CloseRequested += () => fired = true;

        vm.CloseCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void TrackElement_BeforeBeginGroup_IsIgnored()
    {
        var vm = new PreviewViewModel();
        vm.TrackElement(new object()); // no active group — should not throw

        vm.BeginGroup();
        vm.CommitGroup(); // empty group — should not push

        Assert.False(vm.UndoCommand.CanExecute(null));
    }
}
