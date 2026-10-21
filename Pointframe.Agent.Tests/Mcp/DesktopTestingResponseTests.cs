using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopTestingResponseTests
{
    [Fact]
    public void MapObservation_PreservesImageRefsAndHidesSensitiveText()
    {
        var now = DateTimeOffset.UtcNow;
        var process = new DesktopProcessIdentity("process-1", 1, now, "target.exe", "hash");
        var observation = new DesktopObservation(
            DesktopTestingLimits.SchemaVersion,
            "observation-1",
            process,
            DesktopTargetState.Running,
            DesktopObservationStatus.Available,
            [new DesktopImageReference("image-1", 4, 3, new PixelBounds(-4, 5, 4, 3), now)],
            DesktopUiAutomationStatus.Available,
            now);
        var result = new DesktopObservationResult(
            observation,
            new DesktopUiSnapshot(
                DesktopUiAutomationStatus.Available,
                [new DesktopUiElementSnapshot(
                    "element-1",
                    "window-1",
                    "Edit",
                    "Password",
                    null,
                    new PixelBounds(0, 0, 10, 10),
                    true,
                    Text: "secret",
                    IsSensitive: true)],
                now),
            3);

        var mapped = DesktopTestingResponseMapper.MapObservation(result);

        Assert.Equal("image-1", Assert.Single(mapped.Images).ImageRef);
        Assert.Null(Assert.Single(mapped.Elements).Text);
        Assert.Equal("Running", mapped.TargetState);
    }
}
