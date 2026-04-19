using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests;

public sealed class DpiAwarenessScopeTests
{
    [Fact]
    public void RunSystemAware_ExecutesAction()
    {
        StaTestHelper.Run(() =>
        {
            var executed = false;

            DpiAwarenessScope.RunSystemAware(() => executed = true);

            Assert.True(executed);
        });
    }

    [Fact]
    public void RunPerMonitorV2_ExecutesAction()
    {
        StaTestHelper.Run(() =>
        {
            var executed = false;

            DpiAwarenessScope.RunPerMonitorV2(() => executed = true);

            Assert.True(executed);
        });
    }

    [Fact]
    public void RunSystemAware_WhenActionThrows_PropagatesException()
    {
        StaTestHelper.Run(() =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                DpiAwarenessScope.RunSystemAware(() => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", ex.Message);
        });
    }
}
