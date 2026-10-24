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
    public async Task PopupClickAcceptsVisibleHitOnAnotherWindowInTheApprovedProcess()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            ProcessForeground = true,
            ProcessPointVisible = true,
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");
        var popup = new DesktopWindowIdentity("popup", process.ProcessRef, new nint(10));

        var result = await service.ClickAsync(
            new DesktopClickRequest(
                new DesktopInputTarget(popup, BoundsPixels: new PixelBounds(0, 0, 100, 100)),
                10,
                10,
                AcceptAnyVisibleWindowInProcess: true),
            process);

        Assert.True(result.IsValid);
        Assert.Equal(1, adapter.Clicks);
    }

    [Fact]
    public async Task FocusFailureReportsWhenApprovedProcessStillOwnsForeground()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            ProcessForeground = true,
            CanSetForeground = false,
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");
        var popup = new DesktopWindowIdentity("popup", process.ProcessRef, new nint(10));

        var result = await service.FocusAsync(new DesktopInputTarget(popup), process);

        Assert.False(result.IsValid);
        Assert.Equal("FocusFailed", result.Code);
        Assert.True(result.ApprovedProcessForeground);
    }

    [Fact]
    public async Task CloakedForegroundFailureNamesOwnerAndSuggestsUserClickWithoutWindowTitle()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            CanSetForeground = false,
            ForegroundClassName = "Windows.UI.Core.CoreWindow",
            ForegroundProcessName = "LockApp",
            ForegroundCloaked = true,
            ForegroundTitle = "Private window title",
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.FocusAsync(
            new DesktopInputTarget(new DesktopWindowIdentity("window-1", "approved", new nint(10))),
            Process("approved"));

        Assert.False(result.IsValid);
        Assert.Equal("FocusFailed", result.Code);
        Assert.Contains("Windows.UI.Core.CoreWindow", result.Message);
        Assert.Contains("LockApp", result.Message);
        Assert.Contains("PID 42", result.Message);
        Assert.Contains("DWM-cloaked=true", result.Message);
        Assert.Contains("real user click on a normal window", result.Message);
        Assert.DoesNotContain(adapter.ForegroundTitle, result.Message);
    }

    [Fact]
    public async Task UncloakedForegroundFailureNamesOwnerWithoutCloakRecoverySuggestion()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            CanSetForeground = false,
            ForegroundClassName = "CabinetWClass",
            ForegroundProcessName = "explorer",
            ForegroundCloaked = false,
            ForegroundTitle = "Private window title",
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.FocusAsync(
            new DesktopInputTarget(new DesktopWindowIdentity("window-1", "approved", new nint(10))),
            Process("approved"));

        Assert.False(result.IsValid);
        Assert.Equal("FocusFailed", result.Code);
        Assert.Contains("CabinetWClass", result.Message);
        Assert.Contains("explorer", result.Message);
        Assert.Contains("PID 42", result.Message);
        Assert.Contains("DWM-cloaked=false", result.Message);
        Assert.DoesNotContain("real user click", result.Message);
        Assert.DoesNotContain(adapter.ForegroundTitle, result.Message);
    }

    [Fact]
    public async Task FocusRetriesWithAttachedForegroundInputAndThenAllowsPhysicalClick()
    {
        var adapter = new FakeAdapter { Foreground = new nint(20), SetForegroundOnlyWhileAttached = true };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");
        var window = new DesktopWindowIdentity("window-1", process.ProcessRef, new nint(10));

        var focus = await service.FocusAsync(new DesktopInputTarget(window), process);
        var click = await service.ClickAsync(
            new DesktopClickRequest(new DesktopInputTarget(window, BoundsPixels: new PixelBounds(0, 0, 100, 100)), 10, 10),
            process);

        Assert.True(focus.IsValid);
        Assert.True(click.IsValid);
        Assert.Equal(1, adapter.Clicks);
        Assert.Equal(2, adapter.AttachCalls.Count);
        Assert.Equal((11u, 22u, true), adapter.AttachCalls[0]);
        Assert.Equal((11u, 22u, false), adapter.AttachCalls[1]);
        Assert.False(adapter.Attached);
    }

    [Fact]
    public async Task FailedAttachReturnsFocusFailedAndDoesNotClick()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            AttachSucceeds = false,
            SetForegroundOnlyWhileAttached = true,
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));
        var process = Process("approved");
        var window = new DesktopWindowIdentity("window-1", process.ProcessRef, new nint(10));

        var focus = await service.FocusAsync(new DesktopInputTarget(window), process);
        var click = await service.ClickAsync(
            new DesktopClickRequest(new DesktopInputTarget(window, BoundsPixels: new PixelBounds(0, 0, 100, 100)), 10, 10),
            process);

        Assert.False(focus.IsValid);
        Assert.Equal("FocusFailed", focus.Code);
        Assert.False(click.IsValid);
        Assert.Equal(0, adapter.Clicks);
        Assert.Equal(4, adapter.AttachCalls.Count);
        Assert.All(adapter.AttachCalls.Where((_, index) => index % 2 == 1), call => Assert.False(call.Attach));
    }

    [Fact]
    public async Task AttachedForegroundInputIsDetachedWhenAttemptThrows()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            SetForegroundOnlyWhileAttached = true,
            ThrowWhenBringingToTop = true,
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.FocusAsync(
            new DesktopInputTarget(new DesktopWindowIdentity("window-1", "approved", new nint(10))),
            Process("approved"));

        Assert.False(result.IsValid);
        Assert.Equal("FocusFailed", result.Code);
        Assert.Equal(4, adapter.AttachCalls.Count);
        Assert.All(adapter.AttachCalls.Where((_, index) => index % 2 == 1), call => Assert.False(call.Attach));
        Assert.False(adapter.Attached);
    }

    [Fact]
    public async Task FocusSuccessRequiresForegroundWindowRecheck()
    {
        var adapter = new FakeAdapter
        {
            Foreground = new nint(20),
            SetForegroundOnlyWhileAttached = true,
            SetForegroundReportsSuccessWithoutChangingForeground = true,
        };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.FocusAsync(
            new DesktopInputTarget(new DesktopWindowIdentity("window-1", "approved", new nint(10))),
            Process("approved"));

        Assert.False(result.IsValid);
        Assert.Equal("FocusFailed", result.Code);
        Assert.Equal(new nint(20), adapter.GetForegroundWindow());
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
        public int? ScrollX { get; private set; }
        public int? ScrollY { get; private set; }
        public bool? ProcessForeground { get; set; }
        public bool ProcessPointVisible { get; set; }
        public bool CanSetForeground { get; set; } = true;
        public bool SetForegroundOnlyWhileAttached { get; set; }
        public bool AttachSucceeds { get; set; } = true;
        public bool ThrowWhenBringingToTop { get; set; }
        public bool SetForegroundReportsSuccessWithoutChangingForeground { get; set; }
        public string ForegroundClassName { get; set; } = "FakeForegroundClass";
        public string ForegroundProcessName { get; set; } = "foreground-process";
        public bool? ForegroundCloaked { get; set; }
        public string ForegroundTitle { get; set; } = "Not returned by the native adapter";
        public bool Attached { get; private set; }
        public List<(uint AttachTo, uint AttachFrom, bool Attach)> AttachCalls { get; } = [];

        public nint GetForegroundWindow() => Foreground;
        // Unset keeps the interface default (any foreground window counts), which older tests rely on.
        public bool IsForegroundForProcess(DesktopProcessIdentity expectedProcess) => ProcessForeground ?? Foreground != nint.Zero;
        public bool SetForeground(nint handle)
        {
            if (!CanSetForeground || (SetForegroundOnlyWhileAttached && !Attached))
            {
                return false;
            }

            if (!SetForegroundReportsSuccessWithoutChangingForeground)
            {
                Foreground = handle;
            }

            return true;
        }
        public uint GetWindowThreadId(nint handle) => handle == new nint(20) ? 22u : 33u;
        public uint GetCurrentThreadId() => 11;
        public bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach)
        {
            AttachCalls.Add((attachTo, attachFrom, attach));
            if (attach && AttachSucceeds)
            {
                Attached = true;
                return true;
            }

            if (!attach)
            {
                Attached = false;
            }

            return !attach || AttachSucceeds;
        }
        public bool IsWindowMinimized(nint handle) => false;
        public bool RestoreWindow(nint handle) => true;
        public bool BringWindowToTop(nint handle)
        {
            if (ThrowWhenBringingToTop)
            {
                throw new InvalidOperationException("Simulated native failure.");
            }

            return true;
        }
        public int GetWindowProcessId(nint handle) => handle == new nint(20) ? 42 : 10;
        public string GetWindowClassName(nint handle) => ForegroundClassName;
        public string GetWindowProcessName(nint handle) => ForegroundProcessName;
        public bool? IsWindowCloaked(nint handle) => ForegroundCloaked;
        public bool IsWindowValid(nint handle) => true;
        public bool IsWindowVisible(nint handle) => true;
        public bool IsPointVisible(PixelBounds bounds, int x, int y) => x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;
        public bool IsPointVisibleForProcess(PixelBounds bounds, int x, int y, int processId, nint expectedWindow) => ProcessPointVisible && IsPointVisible(bounds, x, y);
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
        public bool SendScroll(int? x, int? y, int detents)
        {
            Scrolls++;
            ScrollX = x;
            ScrollY = y;
            return true;
        }
        public void ReleaseOwnedInput() { }
    }
}
