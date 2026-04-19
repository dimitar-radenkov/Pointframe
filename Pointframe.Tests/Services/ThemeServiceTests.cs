using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ThemeServiceTests
{
    [Fact]
    public void IsDark_Light_ReturnsFalse()
    {
        var svc = new ThemeService();
        Assert.False(svc.IsDark(AppTheme.Light));
    }

    [Fact]
    public void IsDark_Dark_ReturnsTrue()
    {
        var svc = new ThemeService();
        Assert.True(svc.IsDark(AppTheme.Dark));
    }

    [Fact]
    public void IsDark_System_ReturnsBooleanWithoutThrowing()
    {
        var svc = new ThemeService();
        // System theme reads the registry; must not throw regardless of environment.
        var result = svc.IsDark(AppTheme.System);
        Assert.IsType<bool>(result);
    }
}
