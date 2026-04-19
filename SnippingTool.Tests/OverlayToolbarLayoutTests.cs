using System.Windows;
using Xunit;

namespace Pointframe.Tests;

public sealed class OverlayToolbarLayoutTests
{
    [Fact]
    public void Calculate_LargeSelection_KeepsFullActionBarWithoutOverlap()
    {
        var selectionRect = new Rect(400, 220, 700, 320);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
            new Size(1600, 900),
            new Size(42, 430),
            new Size(360, 50),
            new Size(270, 48));

        Assert.Equal(OverlayActionBarMode.Full, layout.ActionBarMode);
        Assert.False(layout.ToolBounds.IntersectsWith(layout.ActionBounds));
        Assert.False(layout.ToolBounds.IntersectsWith(selectionRect));
        Assert.False(layout.ActionBounds.IntersectsWith(selectionRect));
    }

    [Fact]
    public void Calculate_SmallSelection_UsesCompactActionBar()
    {
        var selectionRect = new Rect(550, 170, 260, 180);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
            new Size(1365, 768),
            new Size(42, 430),
            new Size(360, 50),
            new Size(270, 48));

        Assert.Equal(OverlayActionBarMode.Compact, layout.ActionBarMode);
        Assert.False(layout.ToolBounds.IntersectsWith(layout.ActionBounds));
        Assert.False(layout.ToolBounds.IntersectsWith(selectionRect));
        Assert.False(layout.ActionBounds.IntersectsWith(selectionRect));
    }

    [Fact]
    public void Calculate_SelectionNearRightEdge_MovesToolbarAwayFromSelection()
    {
        var selectionRect = new Rect(1250, 180, 240, 220);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
            new Size(1600, 900),
            new Size(42, 430),
            new Size(360, 50),
            new Size(270, 48));

        Assert.True(layout.ToolBounds.Right <= selectionRect.Left || layout.ToolBounds.Left >= selectionRect.Right);
        Assert.False(layout.ToolBounds.IntersectsWith(layout.ActionBounds));
        Assert.False(layout.ActionBounds.IntersectsWith(selectionRect));
    }

    [Fact]
    public void Calculate_WhenNothingFits_UsesFallbackCompactLayout()
    {
        var selectionRect = new Rect(16, 16, 88, 88);
        var layout = OverlayToolbarLayoutHelper.Calculate(
            selectionRect,
            new Size(120, 120),
            new Size(32, 32),
            new Size(32, 32),
            new Size(32, 32));

        Assert.Equal(OverlayActionBarMode.Compact, layout.ActionBarMode);
        Assert.True(layout.ToolBounds.Left >= 0);
        Assert.True(layout.ActionBounds.Bottom <= 120);
    }
}