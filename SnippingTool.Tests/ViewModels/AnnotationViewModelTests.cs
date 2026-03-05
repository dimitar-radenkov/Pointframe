using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public sealed class AnnotationViewModelTests
{
    private static AnnotationGeometryService Geom() => new();

    [Fact]
    public void DefaultTool_IsRectangle()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Assert
        Assert.Equal(AnnotationTool.Rectangle, vm.SelectedTool);
    }

    [Fact]
    public void DefaultColor_IsRed()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Assert
        Assert.Equal(Colors.Red, vm.ActiveColor);
    }

    [Fact]
    public void DefaultStrokeThickness_Is2Point5()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Assert
        Assert.Equal(2.5, vm.StrokeThickness);
    }

    [Fact]
    public void ActiveBrush_MatchesActiveColor()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Act
        vm.ActiveColor = Colors.Blue;

        // Assert
        Assert.Equal(Colors.Blue, vm.ActiveBrush.Color);
    }

    [Fact]
    public void ActiveBrush_PropertyChanged_FiredWhenColorChanges()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act
        vm.ActiveColor = Colors.Green;

        // Assert
        Assert.Contains(nameof(vm.ActiveBrush), raised);
    }

    [Fact]
    public void SelectedTool_PropertyChanged_Fired()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedTool))
            {
                raised = true;
            }
        };

        // Act
        vm.SelectedTool = AnnotationTool.Pen;

        // Assert
        Assert.True(raised);
    }

    [Fact]
    public void StrokeThickness_PropertyChanged_Fired()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StrokeThickness))
            {
                raised = true;
            }
        };

        // Act
        vm.StrokeThickness = 5.0;

        // Assert
        Assert.True(raised);
    }

    [Fact]
    public void BeginDrawing_SetsIsDragging()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Act
        vm.BeginDrawing(new System.Windows.Point(10, 20));

        // Assert
        Assert.True(vm.IsDragging);
    }

    [Fact]
    public void CommitDrawing_ClearsIsDragging()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginDrawing(new System.Windows.Point(10, 20));

        // Act
        vm.CommitDrawing();

        // Assert
        Assert.False(vm.IsDragging);
    }

    [Fact]
    public void CancelDrawing_ClearsIsDragging()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginDrawing(new System.Windows.Point(10, 20));

        // Act
        vm.CancelDrawing();

        // Assert
        Assert.False(vm.IsDragging);
    }

    [Fact]
    public void UpdateDrawing_UpdatesDragCurrent()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginDrawing(new System.Windows.Point(0, 0));

        // Act
        vm.UpdateDrawing(new System.Windows.Point(50, 80));

        // Assert
        Assert.Equal(new System.Windows.Point(50, 80), vm.DragCurrent);
    }

    [Fact]
    public void TryGetShapeParameters_TooSmall_ReturnsNull()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Rectangle;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(1, 1));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void TryGetShapeParameters_Rectangle_ReturnsRectParams()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Rectangle;
        vm.BeginDrawing(new System.Windows.Point(10, 10));
        vm.UpdateDrawing(new System.Windows.Point(60, 60));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.IsType<SnippingTool.Models.RectShapeParameters>(result);
    }

    [Fact]
    public void TryGetShapeParameters_Arrow_ReturnsArrowParamsWithHead()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Arrow;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(100, 0));

        // Act
        var result = vm.TryGetShapeParameters() as SnippingTool.Models.ArrowShapeParameters;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.ArrowHead.Length);
    }

    [Fact]
    public void TryGetShapeParameters_Circle_ReturnsEllipseParams()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Circle;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(50, 50));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.IsType<SnippingTool.Models.EllipseShapeParameters>(result);
    }

    // Every drag-producing tool must return a non-null ShapeParameters.
    // If a new tool is added to the enum but forgotten in TryGetShapeParameters,
    // this test will fail and catch the omission.
    [Theory]
    [InlineData(AnnotationTool.Arrow)]
    [InlineData(AnnotationTool.Rectangle)]
    [InlineData(AnnotationTool.Highlight)]
    [InlineData(AnnotationTool.Pen)]
    [InlineData(AnnotationTool.Line)]
    [InlineData(AnnotationTool.Circle)]
    public void TryGetShapeParameters_AllDragTools_ReturnNonNull(AnnotationTool tool)
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = tool;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(100, 100));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.NotNull(result);
    }

    // Click-only tools must NOT attempt to produce drag geometry.
    [Theory]
    [InlineData(AnnotationTool.Text)]
    [InlineData(AnnotationTool.Number)]
    public void TryGetShapeParameters_ClickOnlyTools_ReturnNull(AnnotationTool tool)
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = tool;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(100, 100));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void IncrementNumberCounter_IncrementsEachCall()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Act
        var first = vm.IncrementNumberCounter();
        var second = vm.IncrementNumberCounter();

        // Assert
        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void ResetNumberCounter_SetsValue()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.IncrementNumberCounter();
        vm.IncrementNumberCounter();

        // Act
        vm.ResetNumberCounter(0);

        // Assert
        Assert.Equal(0, vm.NumberCounter);
    }

    [Theory]
    [InlineData("Red")]
    [InlineData("Blue")]
    [InlineData("Black")]
    [InlineData("Green")]
    [InlineData("Orange")]
    [InlineData("Purple")]
    [InlineData("White")]
    [InlineData("Pink")]
    public void SetColorFromTag_KnownTag_ChangesActiveColor(string tag)
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.ActiveColor = Colors.Transparent;

        // Act
        vm.SetColorFromTag(tag);

        // Assert
        Assert.NotEqual(Colors.Transparent, vm.ActiveColor);
    }

    [Fact]
    public void SetColorFromTag_UnknownTag_FallsBackToRed()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Act
        vm.SetColorFromTag("NotAColor");

        // Assert
        Assert.Equal(Colors.Red, vm.ActiveColor);
    }

    [Fact]
    public void SetColorFromTag_Null_FallsBackToRed()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Act
        vm.SetColorFromTag(null);

        // Assert
        Assert.Equal(Colors.Red, vm.ActiveColor);
    }

    [Fact]
    public void SetStrokeThicknessFromText_ValidNumber_SetsThickness()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());

        // Act
        vm.SetStrokeThicknessFromText("4");

        // Assert
        Assert.Equal(4.0, vm.StrokeThickness);
    }

    [Fact]
    public void SetStrokeThicknessFromText_InvalidText_NoOp()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        var original = vm.StrokeThickness;

        // Act
        vm.SetStrokeThicknessFromText("px");

        // Assert
        Assert.Equal(original, vm.StrokeThickness);
    }

    [Fact]
    public void UndoCommand_CannotExecute_WhenStackEmpty()
    {
        var vm = new TestAnnotationViewModel(Geom());
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void RedoCommand_CannotExecute_WhenStackEmpty()
    {
        var vm = new TestAnnotationViewModel(Geom());
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void CommitGroup_WithElements_EnablesUndo()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();

        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void CommitGroup_Empty_DoesNotEnableUndo()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginGroup();
        vm.CommitGroup();

        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void Undo_MovesGroupToRedoStack()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();

        vm.UndoCommand.Execute(null);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void Undo_FiresUndoApplied_WithCorrectGroup()
    {
        var vm = new TestAnnotationViewModel(Geom());
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
        var vm = new TestAnnotationViewModel(Geom());
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
        var vm = new TestAnnotationViewModel(Geom());
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
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();
        vm.UndoCommand.Execute(null);

        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();

        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void UndoCount_TracksStackDepth()
    {
        var vm = new TestAnnotationViewModel(Geom());

        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();
        Assert.Equal(1, vm.UndoCount);

        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();
        Assert.Equal(2, vm.UndoCount);

        vm.UndoCommand.Execute(null);
        Assert.Equal(1, vm.UndoCount);
    }

    [Fact]
    public void TrackElement_BeforeBeginGroup_IsIgnored()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.TrackElement(new object());

        vm.BeginGroup();
        vm.CommitGroup();

        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // Concrete subclass so we can instantiate the abstract-like partial base
    private sealed partial class TestAnnotationViewModel(AnnotationGeometryService geom)
        : AnnotationViewModel(geom, NullLogger<AnnotationViewModel>.Instance, new FakeUserSettingsService())
    { }
}
