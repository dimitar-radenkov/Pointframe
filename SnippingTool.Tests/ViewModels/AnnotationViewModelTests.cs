using System.Windows.Media;
using SnippingTool.Services;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public class AnnotationViewModelTests
{
    private static AnnotationGeometryService Geom() => new();

    [Fact]
    public void DefaultTool_IsRectangle()
    {
        var vm = new TestAnnotationViewModel(Geom());
        Assert.Equal(AnnotationTool.Rectangle, vm.SelectedTool);
    }

    [Fact]
    public void DefaultColor_IsRed()
    {
        var vm = new TestAnnotationViewModel(Geom());
        Assert.Equal(Colors.Red, vm.ActiveColor);
    }

    [Fact]
    public void DefaultStrokeThickness_Is2Point5()
    {
        var vm = new TestAnnotationViewModel(Geom());
        Assert.Equal(2.5, vm.StrokeThickness);
    }

    [Fact]
    public void ActiveBrush_MatchesActiveColor()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.ActiveColor = Colors.Blue;
        Assert.Equal(Colors.Blue, vm.ActiveBrush.Color);
    }

    [Fact]
    public void ActiveBrush_PropertyChanged_FiredWhenColorChanges()
    {
        var vm = new TestAnnotationViewModel(Geom());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ActiveColor = Colors.Green;

        Assert.Contains(nameof(vm.ActiveBrush), raised);
    }

    [Fact]
    public void SelectedTool_PropertyChanged_Fired()
    {
        var vm = new TestAnnotationViewModel(Geom());
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedTool))
            {
                raised = true;
            }
        };

        vm.SelectedTool = AnnotationTool.Pen;

        Assert.True(raised);
    }

    [Fact]
    public void StrokeThickness_PropertyChanged_Fired()
    {
        var vm = new TestAnnotationViewModel(Geom());
        var raised = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StrokeThickness))
            {
                raised = true;
            }
        };

        vm.StrokeThickness = 5.0;

        Assert.True(raised);
    }

    // ── Drawing state machine ──────────────────────────────────────────────

    [Fact]
    public void BeginDrawing_SetsIsDragging()
    {
        var vm = new TestAnnotationViewModel(Geom());

        vm.BeginDrawing(new System.Windows.Point(10, 20));

        Assert.True(vm.IsDragging);
    }

    [Fact]
    public void CommitDrawing_ClearsIsDragging()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginDrawing(new System.Windows.Point(10, 20));

        vm.CommitDrawing();

        Assert.False(vm.IsDragging);
    }

    [Fact]
    public void CancelDrawing_ClearsIsDragging()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginDrawing(new System.Windows.Point(10, 20));

        vm.CancelDrawing();

        Assert.False(vm.IsDragging);
    }

    [Fact]
    public void UpdateDrawing_UpdatesDragCurrent()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.BeginDrawing(new System.Windows.Point(0, 0));

        vm.UpdateDrawing(new System.Windows.Point(50, 80));

        Assert.Equal(new System.Windows.Point(50, 80), vm.DragCurrent);
    }

    // ── TryGetShapeParameters ──────────────────────────────────────────────

    [Fact]
    public void TryGetShapeParameters_TooSmall_ReturnsNull()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Rectangle;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(1, 1)); // too small

        Assert.Null(vm.TryGetShapeParameters());
    }

    [Fact]
    public void TryGetShapeParameters_Rectangle_ReturnsRectParams()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Rectangle;
        vm.BeginDrawing(new System.Windows.Point(10, 10));
        vm.UpdateDrawing(new System.Windows.Point(60, 60));

        var result = vm.TryGetShapeParameters();

        Assert.IsType<SnippingTool.Models.RectShapeParameters>(result);
    }

    [Fact]
    public void TryGetShapeParameters_Arrow_ReturnsArrowParamsWithHead()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Arrow;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(100, 0));

        var result = vm.TryGetShapeParameters() as SnippingTool.Models.ArrowShapeParameters;

        Assert.NotNull(result);
        Assert.Equal(3, result.ArrowHead.Length);
    }

    [Fact]
    public void TryGetShapeParameters_Circle_ReturnsEllipseParams()
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = AnnotationTool.Circle;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(50, 50));

        var result = vm.TryGetShapeParameters();

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
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = tool;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(100, 100));

        Assert.NotNull(vm.TryGetShapeParameters());
    }

    // Click-only tools must NOT attempt to produce drag geometry.
    [Theory]
    [InlineData(AnnotationTool.Text)]
    [InlineData(AnnotationTool.Number)]
    public void TryGetShapeParameters_ClickOnlyTools_ReturnNull(AnnotationTool tool)
    {
        var vm = new TestAnnotationViewModel(Geom());
        vm.SelectedTool = tool;
        vm.BeginDrawing(new System.Windows.Point(0, 0));
        vm.UpdateDrawing(new System.Windows.Point(100, 100));

        Assert.Null(vm.TryGetShapeParameters());
    }

    // Concrete subclass so we can instantiate the abstract-like partial base
    private sealed partial class TestAnnotationViewModel(AnnotationGeometryService geom) : AnnotationViewModel(geom) { }
}
