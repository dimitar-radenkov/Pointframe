using Pointframe.Engine.Automation.Models;
using Xunit;

namespace Pointframe.AutomationTests.Support;

public sealed class McpDesktopTestInfrastructureTests
{
    [Fact]
    public void ManifestSeparatesExternalAssertionsAndCapturesUnmodifiedLaunch()
    {
        var executable = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(executable, [1, 2, 3]);
        try
        {
            var manifest = new McpRunManifest
            {
                ActualArguments = [],
                OrdinaryStartup = true,
                InitialObservedState = "running-without-main-window",
            };
            manifest.CaptureTargetHashBefore(executable);
            manifest.AddExternalAssertion("processExited", true);
            manifest.CaptureTargetHashAfter();
            manifest.Complete();

            Assert.True(manifest.OrdinaryStartup);
            Assert.Empty(manifest.ActualArguments);
            Assert.Single(manifest.ExternalAssertions);
            Assert.Equal(manifest.TargetExecutableSha256Before, manifest.TargetExecutableSha256After);
            Assert.NotNull(manifest.EndedUtc);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Fact]
    public void MetricsComputesP95AndTracksOnlyExecutedCells()
    {
        var metrics = new DesktopAcceptanceMetrics();
        metrics.RecordObservation(TimeSpan.FromMilliseconds(100));
        metrics.RecordObservation(TimeSpan.FromMilliseconds(200));
        metrics.RecordObservation(TimeSpan.FromMilliseconds(300));
        metrics.MarkMatrixCell("100%", true);
        metrics.MarkMatrixCell("150%", false);

        Assert.Equal(TimeSpan.FromMilliseconds(300), metrics.ObservationP95());
        Assert.Contains("100%", metrics.CompletedMatrixCells);
        Assert.DoesNotContain("150%", metrics.CompletedMatrixCells);
    }

    [Fact]
    public void NavigatorDoesNotEnableDisallowedActions()
    {
        var executable = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(executable, [1]);
        try
        {
            var profile = BlackBoxAppProfileFactory.CreateNotepadProfile(executable);
            Assert.DoesNotContain(
                DesktopTestingAction.PressKeys,
                profile.AllowedActions);
        }
        finally
        {
            File.Delete(executable);
        }
    }
}
