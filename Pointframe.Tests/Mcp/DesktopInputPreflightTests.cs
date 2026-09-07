using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopInputPreflightTests
{
    [Fact]
    public async Task ClickRejectsDifferentProcessBeforeDispatch()
    {
        var adapter = new FakeAdapter();
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var targetProcess = Process("approved");
        var target = new DesktopWindowIdentity("window-1", "other", new nint(10));

        var result = await service.ClickAsync(
            new DesktopClickRequest(new DesktopInputTarget(target, BoundsPixels: new PixelBounds(0, 0, 100, 100)), 10, 10),
            targetProcess);

        Assert.False(result.IsValid);
        Assert.Equal("ProcessMismatch", result.Code);
        Assert.Equal(0, adapter.Clicks);
    }

    [Fact]
    public async Task ClickRejectsUnfocusedOrOutOfBoundsPoint()
    {
        var adapter = new FakeAdapter { Foreground = new nint(10) };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");
        var window = new DesktopWindowIdentity("window-1", process.ProcessRef, new nint(10));

        var result = await service.ClickAsync(
            new DesktopClickRequest(new DesktopInputTarget(window, BoundsPixels: new PixelBounds(20, 20, 10, 10)), 0, 0),
            process);

        Assert.False(result.IsValid);
        Assert.Equal("OccludedOrOutOfBounds", result.Code);
        Assert.Equal(0, adapter.Clicks);
    }

    [Fact]
    public async Task ObservationTargetRejectsWhenApprovedProcessIsNotForeground()
    {
        var adapter = new FakeAdapter();
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");

        var result = await service.ClickAsync(
            new DesktopClickRequest(
                new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 100, 100)),
                10,
                10),
            process);

        Assert.False(result.IsValid);
        Assert.Equal("FocusRequired", result.Code);
        Assert.Equal(0, adapter.Clicks);
    }

    [Fact]
    public async Task KeyPressRejectsMoreThanFourKeys()
    {
        var service = new WindowsDesktopInputService(new FakeFactory(new FakeAdapter()));
        var result = await service.PressKeysAsync(
            new DesktopKeyPressRequest(null, [1, 2, 3, 4, 5], DesktopInputMethod.GlobalHotkey, "capture"),
            Process("approved"));

        Assert.False(result.IsValid);
        Assert.Equal("InvalidKeyCount", result.Code);
    }

    [Fact]
    public async Task TextAndScrollRejectPointsOutsideApprovedBounds()
    {
        var adapter = new FakeAdapter { Foreground = new nint(10) };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");
        var target = new DesktopInputTarget(BoundsPixels: new PixelBounds(10, 10, 20, 20));

        var text = await service.EnterTextAsync(
            new DesktopTextRequest(target, "hello", X: 0, Y: 0),
            process);
        var scroll = await service.ScrollAsync(
            new DesktopScrollRequest(target, 1, X: 0, Y: 0),
            process);

        Assert.Equal("OccludedOrOutOfBounds", text.Code);
        Assert.Equal("OccludedOrOutOfBounds", scroll.Code);
        Assert.Equal(0, adapter.TextInputs);
        Assert.Equal(0, adapter.Scrolls);
    }

    private static DesktopProcessIdentity Process(string processRef) =>
        new(processRef, 10, DateTimeOffset.UtcNow, "target.exe", "hash");

    private sealed class FakeFactory(FakeAdapter adapter) : IDesktopInputNativeAdapterFactory
    {
        public IDesktopInputNativeAdapter Create() => adapter;
    }

    private sealed class FakeAdapter : IDesktopInputNativeAdapter
    {
        public nint Foreground { get; set; }
        public int Clicks { get; private set; }
        public int TextInputs { get; private set; }
        public int Scrolls { get; private set; }

        public nint GetForegroundWindow() => Foreground;
        public bool SetForeground(nint handle)
        {
            Foreground = handle;
            return true;
        }
        public bool IsWindowValid(nint handle) => true;
        public bool IsWindowVisible(nint handle) => true;
        public bool IsPointVisible(PixelBounds bounds, int x, int y) => x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;
        public bool SendClick(int x, int y, bool rightButton, int count)
        {
            Clicks += count;
            return true;
        }
        public bool SendKeys(IReadOnlyList<ushort> virtualKeys) => true;
        public bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds) => true;
        public bool SendUnicodeText(string text)
        {
            TextInputs++;
            return true;
        }
        public bool SendScroll(int detents)
        {
            Scrolls++;
            return true;
        }
        public void ReleaseOwnedInput() { }
    }
}
