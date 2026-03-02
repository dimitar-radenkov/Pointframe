using System.Windows.Media;
using SnippingTool.ViewModels;
using Xunit;

namespace SnippingTool.Tests.ViewModels;

public class AnnotationViewModelTests
{
    [Fact]
    public void DefaultTool_IsArrow()
    {
        var vm = new TestAnnotationViewModel();
        Assert.Equal(AnnotationTool.Arrow, vm.SelectedTool);
    }

    [Fact]
    public void DefaultColor_IsRed()
    {
        var vm = new TestAnnotationViewModel();
        Assert.Equal(Colors.Red, vm.ActiveColor);
    }

    [Fact]
    public void DefaultStrokeThickness_Is2Point5()
    {
        var vm = new TestAnnotationViewModel();
        Assert.Equal(2.5, vm.StrokeThickness);
    }

    [Fact]
    public void ActiveBrush_MatchesActiveColor()
    {
        var vm = new TestAnnotationViewModel();
        vm.ActiveColor = Colors.Blue;
        Assert.Equal(Colors.Blue, vm.ActiveBrush.Color);
    }

    [Fact]
    public void ActiveBrush_PropertyChanged_FiredWhenColorChanges()
    {
        var vm = new TestAnnotationViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ActiveColor = Colors.Green;

        Assert.Contains(nameof(vm.ActiveBrush), raised);
    }

    [Fact]
    public void SelectedTool_PropertyChanged_Fired()
    {
        var vm = new TestAnnotationViewModel();
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
        var vm = new TestAnnotationViewModel();
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

    // Concrete subclass so we can instantiate the abstract-like partial base
    private sealed partial class TestAnnotationViewModel : AnnotationViewModel { }
}
