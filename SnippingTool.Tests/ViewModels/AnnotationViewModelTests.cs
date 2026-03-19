using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
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
    public void TryGetShapeParameters_Highlight_ReturnsHighlightParams()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Highlight;
        vm.BeginDrawing(new System.Windows.Point(10, 10));
        vm.UpdateDrawing(new System.Windows.Point(60, 60));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.IsType<HighlightShapeParameters>(result);
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

    [Fact]
    public void TryGetShapeParameters_Blur_ReturnsBlurParams()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Blur;
        vm.BeginDrawing(new System.Windows.Point(10, 20));
        vm.UpdateDrawing(new System.Windows.Point(110, 120));

        // Act
        var result = vm.TryGetShapeParameters() as SnippingTool.Models.BlurShapeParameters;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
        Assert.Equal(100, result.Width);
        Assert.Equal(100, result.Height);
    }

    [Fact]
    public void TryGetShapeParameters_Blur_TooSmall_ReturnsNull()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Blur;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(1, 1));

        // Act
        var result = vm.TryGetShapeParameters();

        // Assert
        Assert.Null(result);
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
    [InlineData(AnnotationTool.Blur)]
    [InlineData(AnnotationTool.Callout)]
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
    public void TryGetShapeParameters_Callout_ReturnsCalloutParamsWithCorrectGeometry()
    {
        // Arrange
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Callout;
        vm.BeginDrawing(new System.Windows.Point(10, 20));
        vm.UpdateDrawing(new System.Windows.Point(110, 120));

        // Act
        var result = vm.TryGetShapeParameters() as CalloutShapeParameters;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
        Assert.Equal(100, result.Width);
        Assert.Equal(100, result.Height);
        Assert.Equal(string.Empty, result.Text);
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
    public void Undo_PublishesUndoGroupMessage_WithCorrectGroup()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var vm = new TestAnnotationViewModel(Geom(), eventAggregator);
        var element = new object();
        vm.BeginGroup();
        vm.TrackElement(element);
        vm.CommitGroup();

        var recorder = new GroupMessageRecorder();
        using var subscription = eventAggregator.Subscribe<UndoGroupMessage>(recorder.HandleUndoAsync);
        vm.UndoCommand.Execute(null);

        Assert.NotNull(recorder.UndoElements);
        Assert.Contains(element, recorder.UndoElements!);
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
    public void Redo_PublishesRedoGroupMessage_WithCorrectGroup()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var vm = new TestAnnotationViewModel(Geom(), eventAggregator);
        var element = new object();
        vm.BeginGroup();
        vm.TrackElement(element);
        vm.CommitGroup();
        vm.UndoCommand.Execute(null);

        var recorder = new GroupMessageRecorder();
        using var subscription = eventAggregator.Subscribe<RedoGroupMessage>(recorder.HandleRedoAsync);
        vm.RedoCommand.Execute(null);

        Assert.NotNull(recorder.RedoElements);
        Assert.Contains(element, recorder.RedoElements!);
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
    public void ReplaceTrackedElement_AfterCommitGroup_UndoPublishesReplacement()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var vm = new TestAnnotationViewModel(Geom(), eventAggregator);
        var original = new object();
        var replacement = new object();
        vm.BeginGroup();
        vm.TrackElement(original);
        vm.CommitGroup();
        vm.ReplaceTrackedElement(original, replacement);

        var recorder = new GroupMessageRecorder();
        using var subscription = eventAggregator.Subscribe<UndoGroupMessage>(recorder.HandleUndoAsync);

        vm.UndoCommand.Execute(null);

        Assert.NotNull(recorder.UndoElements);
        Assert.Contains(replacement, recorder.UndoElements!);
        Assert.DoesNotContain(original, recorder.UndoElements!);
    }

    [Fact]
    public void RemoveTrackedElement_WhenGroupBecomesEmpty_DisablesUndo()
    {
        var vm = new TestAnnotationViewModel(Geom());
        var original = new object();
        vm.BeginGroup();
        vm.TrackElement(original);
        vm.CommitGroup();

        vm.RemoveTrackedElement(original);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal(0, vm.UndoCount);
    }

    [Fact]
    public void Undo_WhenSubscriberThrows_DoesNotMutateStacks()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var vm = new TestAnnotationViewModel(Geom(), eventAggregator);
        vm.BeginGroup();
        vm.TrackElement(new object());
        vm.CommitGroup();
        using var subscription = eventAggregator.Subscribe<UndoGroupMessage>(_ => throw new InvalidOperationException("boom"));

        var exception = Assert.Throws<InvalidOperationException>(() => vm.UndoCommand.Execute(null));

        Assert.Equal("boom", exception.Message);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.Equal(1, vm.UndoCount);
        Assert.Equal(0, vm.RedoCount);
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
    private sealed partial class TestAnnotationViewModel(AnnotationGeometryService geom, IEventAggregator? eventAggregator = null)
        : AnnotationViewModel(
            geom,
            NullLogger<AnnotationViewModel>.Instance,
            Mock.Of<IUserSettingsService>(s => s.Current == new UserSettings()),
            eventAggregator ?? new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance))
    { }

    private sealed class GroupMessageRecorder
    {
        public IReadOnlyList<object>? UndoElements { get; private set; }

        public IReadOnlyList<object>? RedoElements { get; private set; }

        public ValueTask HandleUndoAsync(UndoGroupMessage message)
        {
            UndoElements = message.Elements;
            return ValueTask.CompletedTask;
        }

        public ValueTask HandleRedoAsync(RedoGroupMessage message)
        {
            RedoElements = message.Elements;
            return ValueTask.CompletedTask;
        }
    }
}
