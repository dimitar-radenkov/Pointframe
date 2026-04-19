using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class AppVersionServiceTests
{
    [Fact]
    public void Current_ReturnsNonNullVersion()
    {
        var service = new AppVersionService();

        Assert.NotNull(service.Current);
    }

    [Fact]
    public void Current_ReturnsSameInstanceEachCall()
    {
        var service = new AppVersionService();

        Assert.Same(service.Current, service.Current);
    }

    [Fact]
    public void Current_MajorAndMinorAreNonNegative()
    {
        var service = new AppVersionService();
        var v = service.Current;

        Assert.True(v.Major >= 0);
        Assert.True(v.Minor >= 0);
    }
}
