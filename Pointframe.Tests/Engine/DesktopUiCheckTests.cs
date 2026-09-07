using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopUiCheckTests
{
    [Fact]
    public async Task CheckAsync_ProviderUnavailableIsInconclusive()
    {
        var source = new FakeSource(new DesktopUiCheckEvaluation(false, false, 0));
        var service = new DesktopUiCheckService(source);

        var result = await service.CheckAsync(
            new DesktopUiCheckCondition.WindowExists("window-1"),
            TimeSpan.FromMilliseconds(100));

        Assert.False(result.StateAvailable);
        Assert.False(result.Matches);
    }

    [Fact]
    public async Task CheckAsync_RequiresSingleMatchForStatePredicates()
    {
        var source = new FakeSource(new DesktopUiCheckEvaluation(true, true, 2));
        var service = new DesktopUiCheckService(source);

        var result = await service.CheckAsync(
            new DesktopUiCheckCondition.Enabled(
                new DesktopLocator(DesktopLocatorKind.AutomationId, AutomationId: "save")),
            TimeSpan.FromMilliseconds(100));

        Assert.False(result.Matches);
        Assert.Equal(2, result.MatchCount);
    }

    private sealed class FakeSource(DesktopUiCheckEvaluation evaluation) : IDesktopUiCheckSource
    {
        public Task<DesktopUiCheckEvaluation> EvaluateAsync(DesktopUiCheckCondition condition, CancellationToken cancellationToken)
        {
            return Task.FromResult(evaluation);
        }
    }
}
