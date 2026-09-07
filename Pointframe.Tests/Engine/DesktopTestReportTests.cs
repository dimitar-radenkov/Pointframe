using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopTestReportTests
{
    [Fact]
    public async Task FinalizeDistinguishesInconclusiveChecks()
    {
        var service = new DesktopTestReportService();
        service.RecordCheck("session-1", new DesktopTestCheckReport(
            "visual check",
            DesktopVerificationStatus.Inconclusive,
            true,
            "external-image",
            "No reliable oracle",
            DateTimeOffset.UtcNow));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("inconclusive", report.Verdict);
        Assert.Single(report.Checks);
        Assert.True(report.Checks[0].External);
    }

    [Fact]
    public async Task FailedCheckWinsOverInconclusive()
    {
        var service = new DesktopTestReportService();
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Inconclusive));
        service.RecordCheck("session-1", Check(DesktopVerificationStatus.Failed));

        var report = await service.FinalizeAsync("session-1");

        Assert.Equal("failed", report.Verdict);
    }

    private static DesktopTestCheckReport Check(DesktopVerificationStatus verdict) =>
        new("check", verdict, false, "server-ui", null, DateTimeOffset.UtcNow);
}
