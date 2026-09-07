using Pointframe.Engine.Automation.Models;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class BlackBoxBoundaryTests
{
    [Fact]
    public void ProfileContractAllowsNormalLaunchWithNoArguments()
    {
        var profile = new BlackBoxAppProfile(
            "target",
            @"C:\runner\target.exe",
            [],
            @"C:\runner",
            false,
            null,
            new HashSet<DesktopTestingAction>(),
            new Dictionary<string, IReadOnlyList<string>>(),
            new HashSet<DesktopSurfaceKind>(),
            false);

        Assert.Empty(profile.Arguments);
        Assert.False(profile.AllowAttach);
        Assert.Empty(profile.AllowedGlobalHotkeys);
        Assert.Empty(profile.AllowedShellSurfaces);
    }

    [Fact]
    public void ScrollBoundsAreExplicitAndFinite()
    {
        Assert.Equal(-10, DesktopTestingLimits.MinScrollDetents);
        Assert.Equal(10, DesktopTestingLimits.MaxScrollDetents);
        Assert.True(DesktopTestingLimits.MaxScrollDetents > DesktopTestingLimits.MinScrollDetents);
    }
}
