using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopScrollTests
{
    [Fact]
    public async Task ScrollAcceptsBoundedNonzeroDetents()
    {
        var adapter = new Adapter();
        var service = new WindowsDesktopInputService(new Factory(adapter));
        var target = new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 20, 20));
        var process = new DesktopProcessIdentity("p", 1, DateTimeOffset.UtcNow, "target.exe", "hash");

        var result = await service.ScrollAsync(new DesktopScrollRequest(target, -10), process);

        Assert.True(result.IsValid);
        Assert.Equal(-10, adapter.Detents);
    }

    private sealed class Factory(Adapter adapter) : IDesktopInputNativeAdapterFactory
    {
        public IDesktopInputNativeAdapter Create() => adapter;
    }

    private sealed class Adapter : IDesktopInputNativeAdapter
    {
        public int Detents { get; private set; }
        public nint GetForegroundWindow() => new nint(1);
        public bool SetForeground(nint handle) => true;
        public bool IsWindowValid(nint handle) => true;
        public bool IsWindowVisible(nint handle) => true;
        public bool IsPointVisible(PixelBounds bounds, int x, int y) => true;
        public bool SendClick(int x, int y, bool rightButton, int count) => true;
        public bool SendKeys(IReadOnlyList<ushort> virtualKeys) => true;
        public bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds) => true;
        public bool SendUnicodeText(string text) => true;
        public bool SendScroll(int detents)
        {
            Detents = detents;
            return true;
        }
        public void ReleaseOwnedInput() { }
    }
}
