using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopTestingContractTests
{
    [Fact]
    public void ImageReference_CreatePoint_RejectsCoordinatesOutsideImage()
    {
        var image = new DesktopImageReference(
            "image-1",
            100,
            80,
            new PixelBounds(10, 20, 100, 80),
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.CreatePoint(100, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.CreatePoint(10, 80));
    }

    [Fact]
    public void DesktopTarget_RejectsUnknownUnionShapeByConstruction()
    {
        Assert.Throws<ArgumentException>(() => new DesktopTarget.Element(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopTarget.ImagePoint("image-1", -1, 0, 100, 80));
    }

    [Fact]
    public void Contracts_RejectUnsupportedSchemaVersion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopActionResult(
            2,
            Guid.NewGuid().ToString(),
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.Complete,
            DesktopVerificationStatus.NotRequested,
            DesktopObservationStatus.NotRequested));
    }

    [Fact]
    public void Observation_AllowsImageCapabilityWhenUiAutomationIsUnavailable()
    {
        var observation = new DesktopObservation(
            DesktopTestingLimits.SchemaVersion,
            "observation-1",
            new DesktopProcessIdentity(
                "process-1",
                1234,
                DateTimeOffset.UtcNow,
                "C:\\Apps\\Target.exe",
                "abc123"),
            DesktopTargetState.Running,
            DesktopObservationStatus.Available,
            [
                new DesktopImageReference(
                    "image-1",
                    10,
                    10,
                    new PixelBounds(0, 0, 10, 10),
                    DateTimeOffset.UtcNow),
            ],
            DesktopUiAutomationStatus.Unavailable,
            DateTimeOffset.UtcNow);

        Assert.Equal(DesktopObservationStatus.Available, observation.ObservationStatus);
        Assert.Equal(DesktopUiAutomationStatus.Unavailable, observation.UiaStatus);
        Assert.Single(observation.Images);
    }

    [Fact]
    public void ActionResult_RequiresErrorForFailedVerification()
    {
        Assert.Throws<ArgumentException>(() => new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            Guid.NewGuid().ToString(),
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.Complete,
            DesktopVerificationStatus.Failed,
            DesktopObservationStatus.Available));
    }

    [Fact]
    public void ActionResult_AllowsPartialDispatchAndInconclusiveVerification()
    {
        var result = new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            Guid.NewGuid().ToString(),
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.Partial,
            DesktopVerificationStatus.Inconclusive,
            DesktopObservationStatus.Unavailable,
            new DesktopOperationError("ProviderTimeout", "The provider stopped responding."));

        Assert.Equal(DesktopDispatchStatus.Partial, result.Dispatch);
        Assert.Equal(DesktopVerificationStatus.Inconclusive, result.Verification);
        Assert.Equal(DesktopObservationStatus.Unavailable, result.ObservationStatus);
    }
}
