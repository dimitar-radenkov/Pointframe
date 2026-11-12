using System.Text.RegularExpressions;
using Xunit;

namespace Pointframe.Tests.Services.Handlers;

[Collection("WpfUi")]
public sealed class StaTestHelperTests
{
    [Fact]
    public void Run_WhenActionExceedsTimeout_ReportsCallerStartElapsedTimeAndLiveThreads()
    {
        using var releaseAction = new ManualResetEventSlim();
        using var actionStarted = new ManualResetEventSlim();

        try
        {
            var exception = Assert.Throws<TimeoutException>(() => StaTestHelper.Run(
                () =>
                {
                    actionStarted.Set();
                    releaseAction.Wait();
                },
                TimeSpan.FromSeconds(2)));

            Assert.True(actionStarted.IsSet);
            Assert.Contains(nameof(Run_WhenActionExceedsTimeout_ReportsCallerStartElapsedTimeAndLiveThreads), exception.Message);
            Assert.Contains(nameof(StaTestHelperTests) + ".cs", exception.Message);
            Assert.Contains("started=True", exception.Message);
            Assert.Matches(@"elapsed=\d+\.\d{2} seconds", exception.Message);
            Assert.Matches(new Regex(@"live StaTestHelper threads=\d+"), exception.Message);
        }
        finally
        {
            releaseAction.Set();
        }
    }
}
