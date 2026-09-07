using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopGestureTests
{
    [Fact]
    public async Task DragRejectsUnboundedPointCountAndDuration()
    {
        var service = new WindowsDesktopInputService(new FakeFactory());
        var process = Process();
        var target = new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 100, 100));

        var points = Enumerable.Range(0, DesktopTestingLimits.MaxDragPoints + 1)
            .Select(index => new PixelBounds(index, index, 1, 1))
            .ToArray();
        var result = await service.DragAsync(new DesktopDragRequest(target, points), process);

        Assert.Equal("InvalidDragPoints", result.Code);
    }

    [Fact]
    public async Task EnterTextRejectsSemanticValueWithoutProviderElement()
    {
        var service = new WindowsDesktopInputService(new FakeFactory());
        var result = await service.EnterTextAsync(
            new DesktopTextRequest(new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 10, 10)), "λ", true),
            Process());

        Assert.Equal("UnsupportedSemanticValue", result.Code);
    }

    [Fact]
    public async Task ScrollRejectsZeroAndOutOfRangeDetents()
    {
        var service = new WindowsDesktopInputService(new FakeFactory());
        var target = new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 10, 10));

        Assert.Equal("InvalidScrollDetents", (await service.ScrollAsync(new DesktopScrollRequest(target, 0), Process())).Code);
        Assert.Equal("InvalidScrollDetents", (await service.ScrollAsync(new DesktopScrollRequest(target, 11), Process())).Code);
    }

    private static DesktopProcessIdentity Process() =>
        new("process", 1, DateTimeOffset.UtcNow, "target.exe", "hash");

    private sealed class FakeFactory : IDesktopInputNativeAdapterFactory
    {
        public IDesktopInputNativeAdapter Create() => new FakeAdapter();
    }

    private sealed class FakeAdapter : IDesktopInputNativeAdapter
    {
        public nint GetForegroundWindow() => 0;
        public bool SetForeground(nint handle) => true;
        public bool IsWindowValid(nint handle) => true;
        public bool IsWindowVisible(nint handle) => true;
        public bool IsPointVisible(PixelBounds bounds, int x, int y) => true;
        public bool SendClick(int x, int y, bool rightButton, int count) => true;
        public bool SendKeys(IReadOnlyList<ushort> virtualKeys) => true;
        public bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds) => true;
        public bool SendUnicodeText(string text) => true;
        public bool SendScroll(int detents) => true;
        public void ReleaseOwnedInput() { }
    }
}
