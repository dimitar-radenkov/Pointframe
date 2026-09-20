using System.Drawing;
using System.Text.Json;
using Pointframe.AutomationTests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.AutomationTests.Smoke;

// Targeted real-desktop verification of two changes: that desktop_observe_app hands back actual
// pixels whose preview coordinates rescale to the desktop, and that native input lands where it is
// aimed (including on a non-primary monitor), holds the button across a drag, and moves the cursor
// before turning the wheel. Only Pointframe.DesktopTestFixture is ever driven.
public sealed class McpDesktopObserveAndInputTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "DesktopAutomation")]
    public async Task ObservePixelsDriveClickDragAndScrollOnMonitor(int monitorIndex)
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var fixtureExecutable = RequireFixtureExecutable();
        var mcpExecutable = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        var artifacts = CreateArtifactDirectory();

        await using var harness = await DesktopFixtureHarness.StartAsync(
            fixtureExecutable,
            mcpExecutable,
            artifacts,
            monitorIndex);

        var displays = await harness.ListDisplaysAsync();
        output.WriteLine($"displays: {string.Join(" | ", displays.Select(Describe))}");
        Skip.If(
            monitorIndex >= displays.Count,
            $"This machine reports {displays.Count} monitor(s); monitor index {monitorIndex} does not exist, so the non-primary half of the input claim cannot be exercised here.");

        var display = displays[monitorIndex];
        if (monitorIndex > 0)
        {
            output.WriteLine(
                $"non-primary monitor origin: ({display.X},{display.Y}); an absolute click without " +
                "MOUSEEVENTF_VIRTUALDESK cannot reach a coordinate outside the primary monitor's rect.");
        }

        var observation = await harness.ObserveAsync(display);

        // --- Claim A: real pixels, and the preview size the structured payload advertises ---
        Assert.Single(observation.Images);
        Assert.Equal(observation.Images.Count, observation.ImageBlockCount);
        var image = observation.Images[0];
        var (decodedWidth, decodedHeight) = DesktopFixtureHarness.DecodePngSize(observation.ImageBlockBytes[0]);
        output.WriteLine(
            $"image_ref={image.ImageRef} preview={image.Width}x{image.Height} decoded={decodedWidth}x{decodedHeight} " +
            $"desktop={image.DesktopWidth}x{image.DesktopHeight}@({image.DesktopX},{image.DesktopY}) " +
            $"png_bytes={observation.ImageBlockBytes[0].Length}");
        Assert.Equal(image.Width, decodedWidth);
        Assert.Equal(image.Height, decodedHeight);
        Assert.Equal(display.Width, image.DesktopWidth);
        Assert.Equal(display.Height, image.DesktopHeight);
        Assert.Equal(1600, Math.Max(image.Width, image.Height));
        Assert.True(
            image.Width != image.DesktopWidth || image.Height != image.DesktopHeight,
            "This capture was not large enough to force a downscale, so the rescale path is untested.");

        // --- Claim A: a coordinate read off the preview rescales to the right desktop pixel ---
        var clickBlock = DesktopFixtureHarness.FindColorBlock(
            observation.ImageBlockBytes[0],
            DesktopFixtureHarness.ClickTargetColor);
        Assert.False(
            clickBlock.IsEmpty,
            $"The fixture click target was not visible on {display.MonitorName}; it is not on this monitor.");
        var clickPoint = Center(clickBlock);
        output.WriteLine($"click target in preview: {clickBlock} -> click at image ({clickPoint.X},{clickPoint.Y})");

        var expectedClickDesktop = ToDesktop(image, clickPoint);
        output.WriteLine($"expected desktop click point: ({expectedClickDesktop.X},{expectedClickDesktop.Y})");

        var before = harness.ReadState();
        var clickResult = await harness.ClickAsync(observation, clickPoint.X, clickPoint.Y);
        AssertDispatched(clickResult, "desktop_click");
        var afterClick = harness.WaitForState(state => state.ClickCount > before.ClickCount, SettleTimeout);
        output.WriteLine($"fixture after click: {afterClick.Raw}");
        Assert.True(
            afterClick.ClickCount > before.ClickCount,
            $"The fixture click target never saw the click. Fixture state: {afterClick.Raw}");
        Assert.InRange(afterClick.LastClickPoint.X, 0, afterClick.ClickTargetSize.Width);
        Assert.InRange(afterClick.LastClickPoint.Y, 0, afterClick.ClickTargetSize.Height);

        // --- Claim B: the pointer is physically on the aimed monitor, not on the primary one ---
        var cursor = DesktopFixtureHarness.GetCursorPosition();
        output.WriteLine($"cursor after click: ({cursor.X},{cursor.Y}) target display {Describe(display)}");
        Assert.InRange(cursor.X, display.X, display.X + display.Width - 1);
        Assert.InRange(cursor.Y, display.Y, display.Y + display.Height - 1);
        Assert.InRange(cursor.X, expectedClickDesktop.X - 2, expectedClickDesktop.X + 2);
        Assert.InRange(cursor.Y, expectedClickDesktop.Y - 2, expectedClickDesktop.Y + 2);

        // --- Claim B: a drag is one held gesture, not two disconnected clicks ---
        var dragObservation = await harness.ObserveAsync(display);
        var dragBlock = DesktopFixtureHarness.FindColorBlock(
            dragObservation.ImageBlockBytes[0],
            DesktopFixtureHarness.DragSurfaceColor);
        Assert.False(dragBlock.IsEmpty, "The fixture drag surface was not visible in the preview image.");
        var dragY = dragBlock.Y + (dragBlock.Height / 2);
        var dragFrom = (X: dragBlock.X + Math.Max(2, dragBlock.Width / 6), Y: dragY);
        var dragTo = (X: dragBlock.Right - Math.Max(3, dragBlock.Width / 6), Y: dragY);
        output.WriteLine($"drag surface in preview: {dragBlock} -> {dragFrom} to {dragTo}");

        var beforeDrag = harness.ReadState();
        var dragResult = await harness.DragAsync(dragObservation, [dragFrom, dragTo], 600);
        AssertDispatched(dragResult, "desktop_drag");
        var afterDrag = harness.WaitForState(state => state.DragUpCount > beforeDrag.DragUpCount, SettleTimeout);
        output.WriteLine($"fixture after drag: {afterDrag.Raw}");
        Assert.Equal(beforeDrag.DragDownCount + 1, afterDrag.DragDownCount);
        Assert.Equal(beforeDrag.DragUpCount + 1, afterDrag.DragUpCount);
        Assert.True(
            afterDrag.DragMoveWhileDownCount >= 3,
            $"Only {afterDrag.DragMoveWhileDownCount} move(s) arrived with the button held; a drag was not delivered. Fixture state: {afterDrag.Raw}");
        Assert.True(
            Math.Abs(afterDrag.DragUpPoint.X - afterDrag.DragDownPoint.X) > 20,
            $"The press and release landed at effectively the same point: {afterDrag.Raw}");

        // --- Claim B: the wheel is preceded by a cursor move to the requested point ---
        var scrollObservation = await harness.ObserveAsync(display);
        var scrollBlock = DesktopFixtureHarness.FindColorBlock(
            scrollObservation.ImageBlockBytes[0],
            DesktopFixtureHarness.ScrollSurfaceColor);
        Assert.False(scrollBlock.IsEmpty, "The fixture scroll surface was not visible in the preview image.");
        var scrollPoint = Center(scrollBlock);
        var expectedScrollDesktop = ToDesktop(scrollObservation.Images[0], scrollPoint);
        output.WriteLine(
            $"scroll surface in preview: {scrollBlock} -> image ({scrollPoint.X},{scrollPoint.Y}) " +
            $"expected desktop ({expectedScrollDesktop.X},{expectedScrollDesktop.Y})");

        var beforeScroll = harness.ReadState();
        var scrollResult = await harness.ScrollAsync(scrollObservation, scrollPoint.X, scrollPoint.Y, -3);
        AssertDispatched(scrollResult, "desktop_scroll");
        var afterScroll = harness.WaitForState(state => state.WheelCount > beforeScroll.WheelCount, SettleTimeout);
        var scrollCursor = DesktopFixtureHarness.GetCursorPosition();
        output.WriteLine($"cursor after scroll: ({scrollCursor.X},{scrollCursor.Y}); fixture: {afterScroll.Raw}");
        Assert.InRange(scrollCursor.X, expectedScrollDesktop.X - 2, expectedScrollDesktop.X + 2);
        Assert.InRange(scrollCursor.Y, expectedScrollDesktop.Y - 2, expectedScrollDesktop.Y + 2);
        Assert.True(
            afterScroll.WheelCount > beforeScroll.WheelCount,
            $"The fixture scroll surface never received the wheel. Fixture state: {afterScroll.Raw}");
        Assert.True(afterScroll.WheelDeltaTotal < beforeScroll.WheelDeltaTotal, "The wheel turned the wrong way.");
    }

    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public async Task ObserveAppOmitsInlineImagesWhenNotRequested()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var fixtureExecutable = RequireFixtureExecutable();
        var mcpExecutable = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        var artifacts = CreateArtifactDirectory();

        await using var harness = await DesktopFixtureHarness.StartAsync(
            fixtureExecutable,
            mcpExecutable,
            artifacts,
            0);

        var display = (await harness.ListDisplaysAsync())[0];
        var withImages = await harness.ObserveAsync(display);
        var withoutImages = await harness.ObserveAsync(display, includeImages: false);

        output.WriteLine($"blocks with images: {withImages.ImageBlockCount}; without: {withoutImages.ImageBlockCount}");
        Assert.Equal(1, withImages.ImageBlockCount);
        Assert.Equal(0, withoutImages.ImageBlockCount);
        Assert.Single(withoutImages.Images);
        Assert.Equal(withImages.Images[0].Width, withoutImages.Images[0].Width);
        Assert.Equal(withImages.Images[0].Height, withoutImages.Images[0].Height);
    }

    private static string RequireFixtureExecutable()
    {
        var path = Environment.GetEnvironmentVariable(DesktopFixtureHarness.FixtureExecutableVariable);
        Skip.If(
            string.IsNullOrWhiteSpace(path) || !File.Exists(path),
            $"Set {DesktopFixtureHarness.FixtureExecutableVariable} to the published Pointframe.DesktopTestFixture.exe.");
        return Path.GetFullPath(path!);
    }

    private static string CreateArtifactDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pointframe-desktop-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AssertDispatched(JsonElement result, string tool)
    {
        var structured = result.GetProperty("structuredContent");
        var dispatch = structured.GetProperty("dispatch").GetString();
        Assert.True(
            dispatch is "Complete",
            $"{tool} did not dispatch cleanly: {structured}");
    }

    private static (int X, int Y) Center(Rectangle block) =>
        (block.X + (block.Width / 2), block.Y + (block.Height / 2));

    private static (int X, int Y) ToDesktop(FixtureObservationImage image, (int X, int Y) point) =>
        (image.DesktopX + (int)Math.Floor(point.X * (double)image.DesktopWidth / image.Width),
            image.DesktopY + (int)Math.Floor(point.Y * (double)image.DesktopHeight / image.Height));

    private static string Describe(FixtureDisplay display) =>
        $"{display.MonitorName} {display.Width}x{display.Height}@({display.X},{display.Y})";
}
