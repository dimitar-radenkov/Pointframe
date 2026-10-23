using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopSelectorActionTests
{
    private const string SessionId = "session-selector";
    private const string ProcessRef = "process-1";
    private const string WindowA = "window-process-1-A";
    private const string WindowB = "window-process-1-B";

    private static DesktopUiElementSnapshot Element(string elementRef, string windowRef, string role, string? name, string? automationId, bool enabled = true) =>
        new(elementRef, windowRef, role, name, automationId, new PixelBounds(100, 200, 40, 20), enabled);

    private static DesktopUiSnapshot Snapshot(bool truncated, params DesktopUiElementSnapshot[] elements) =>
        new(DesktopUiAutomationStatus.Available, elements, DateTimeOffset.UtcNow, truncated);

    [Fact]
    public async Task UniqueAutomationIdSelectorInvokesTheFreshlyResolvedElement()
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Button", "Save", "saveButton")));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("Complete", response.Dispatch);
        Assert.Equal("click", response.Method);
        Assert.Equal(2, fixture.InspectionCount);
        fixture.Input.Verify(input => input.ClickAsync(
            It.Is<DesktopClickRequest>(click => click.X == 120 && click.Y == 210 && click.Target.Window!.WindowRef == WindowA),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Observations.Verify(service => service.ResolveElement(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RoleAndNameSelectorFallsBackToThePatternWithTheFreshRef()
    {
        var fixture = new Fixture(Snapshot(false, new DesktopUiElementSnapshot("el-9-1", WindowA, "Button", "Save", null, new PixelBounds(0, 0, 0, 0), true)));
        fixture.UiAutomation.Setup(provider => provider.TryInvoke("el-9-1")).Returns(UiInvokeOutcome.Completed);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), role: "Button", name: "Save");

        Assert.Equal("pattern", response.Method);
        Assert.Equal("Complete", response.Dispatch);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke("el-9-1"), Times.Once);
    }

    [Fact]
    public async Task DuplicateSelectorIsAmbiguousAndSendsNothing()
    {
        var fixture = new Fixture(Snapshot(false,
            Element("el-9-1", WindowA, "Button", "Save", "a"),
            Element("el-9-2", WindowB, "Button", "Save", "b")));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), role: "Button", name: "Save");

        Assert.Equal("AmbiguousTarget", response.Error?.Code);
        Assert.Equal("NotStarted", response.Dispatch);
        Assert.Contains(WindowB, response.Error!.Message, StringComparison.Ordinal);
        Assert.Null(response.NextStep!.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(response.NextStep.Reason));
        fixture.AssertNothingSent();
    }

    [Fact]
    public async Task MissingSelectorIsElementNotFoundAndSendsNothing()
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Button", "Open", "openButton")));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("ElementNotFound", response.Error?.Code);
        Assert.Equal("NotStarted", response.Dispatch);
        fixture.AssertNothingSent();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TruncatedInspectionWithoutTwoVisibleMatchesIsInspectionIncomplete(int visibleMatches)
    {
        var elements = Enumerable.Range(0, visibleMatches)
            .Select(index => Element($"el-9-{index}", WindowA, "Button", "Save", "saveButton"))
            .Append(Element("el-9-x", WindowA, "Edit", "Box", "textBox"))
            .ToArray();
        var fixture = new Fixture(Snapshot(true, elements));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("InspectionIncomplete", response.Error?.Code);
        Assert.Equal("NotStarted", response.Dispatch);
        Assert.Null(response.NextStep!.Arguments);
        fixture.AssertNothingSent();
    }

    [Fact]
    public async Task TruncatedInspectionWithTwoVisibleMatchesIsAmbiguousAsOnACompleteTree()
    {
        var fixture = new Fixture(Snapshot(true,
            Element("el-9-1", WindowA, "Button", "Save", "saveButton"),
            Element("el-9-2", WindowB, "Button", "Save", "saveButton")));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("AmbiguousTarget", response.Error?.Code);
        fixture.AssertNothingSent();
    }

    [Fact]
    public async Task UnavailableInspectionNeverResolves()
    {
        var fixture = new Fixture(new DesktopUiSnapshot(DesktopUiAutomationStatus.Unavailable, [], DateTimeOffset.UtcNow, ErrorCode: "ProviderUnavailable"));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("ProviderUnavailable", response.Error?.Code);
        fixture.AssertNothingSent();
    }

    [Fact]
    public async Task WindowRefScopesTheSelectorToItsWindow()
    {
        var elements = new[]
        {
            Element("el-9-1", WindowA, "Button", "OK", "okButton"),
            Element("el-9-2", WindowB, "Button", "OK", "okButton"),
        };
        var scoped = new Fixture(Snapshot(false, elements));
        var elsewhere = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Button", "OK", "okButton")));

        var inside = await scoped.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "okButton", windowRef: WindowB);
        var outside = await elsewhere.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "okButton", windowRef: WindowB);

        Assert.Equal("Complete", inside.Dispatch);
        scoped.Input.Verify(input => input.ClickAsync(
            It.Is<DesktopClickRequest>(click => click.Target.Window!.WindowRef == WindowB),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("ElementNotFound", outside.Error?.Code);
    }

    [Theory]
    [InlineData("el-1", "saveButton", null, null, null)]
    [InlineData("el-1", null, "Button", "Save", null)]
    [InlineData("el-1", null, null, null, WindowA)]
    [InlineData(null, null, null, null, null)]
    [InlineData(null, "saveButton", "Button", "Save", null)]
    [InlineData(null, null, "Button", null, null)]
    [InlineData(null, null, null, "Save", null)]
    [InlineData(null, null, null, null, WindowA)]
    public async Task InvokeNeedsExactlyOneWayToNameTheElement(string? elementRef, string? automationId, string? role, string? name, string? windowRef)
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Button", "Save", "saveButton")));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), elementRef, automationId, role, name, windowRef);

        Assert.Equal("InvalidRequest", response.Error?.Code);
        Assert.Equal(0, fixture.InspectionCount);
        fixture.AssertNothingSent();
    }

    [Fact]
    public async Task BlankElementRefStillReportsElementRequired()
    {
        var fixture = new Fixture(Snapshot(false));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), " ");

        Assert.Equal("ElementRequired", response.Error?.Code);
    }

    [Fact]
    public async Task ElementRefCallsKeepUsingTheObservedElementWithoutInspecting()
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.Observations.Setup(service => service.ResolveElement("el-1-2", ProcessRef))
            .Returns(new DesktopObservedElement("observation-1", Element("el-1-2", WindowA, "Button", "Open", "open")));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), "el-1-2");

        Assert.Equal("click", response.Method);
        Assert.Equal(0, fixture.InspectionCount);
    }

    [Fact]
    public async Task StaleElementRefStillFallsToThePatternAndFails()
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.UiAutomation.Setup(provider => provider.TryInvoke("el-gone")).Returns(UiInvokeOutcome.Failed);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), "el-gone");

        Assert.Equal("pattern", response.Method);
        Assert.Equal("InputDispatchFailed", response.Error?.Code);
        Assert.Equal(0, fixture.InspectionCount);
    }

    [Fact]
    public async Task SemanticEntryBySelectorNeedsNoObservationOrCoordinates()
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Edit", "Name", "nameBox")));
        fixture.UiAutomation.Setup(provider => provider.TrySetValue("el-9-1", "hello")).Returns(true);

        var response = await fixture.Tools.EnterTextAsync(
            SessionId, Guid.NewGuid().ToString(), "hello", semanticValue: true, automationId: "nameBox");

        Assert.Equal("Complete", response.Dispatch);
        Assert.Null(response.Error);
        fixture.UiAutomation.Verify(provider => provider.TrySetValue("el-9-1", "hello"), Times.Once);
        fixture.Observations.Verify(service => service.Resolve(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SemanticEntryBySelectorAcceptsEmptyAndWhitespaceText(string text)
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Edit", "Name", "nameBox")));
        fixture.UiAutomation.Setup(provider => provider.TrySetValue("el-9-1", text)).Returns(true);

        var response = await fixture.Tools.EnterTextAsync(
            SessionId, Guid.NewGuid().ToString(), text, semanticValue: true, automationId: "nameBox");

        Assert.Equal("Complete", response.Dispatch);
        Assert.Null(response.Error);
        fixture.UiAutomation.Verify(provider => provider.TrySetValue("el-9-1", text), Times.Once);
    }

    [Fact]
    public async Task SemanticEntryWithMissingTextIsInvalidAndDispatchesNothing()
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Edit", "Name", "nameBox")));

        var response = await fixture.Tools.EnterTextAsync(
            SessionId, Guid.NewGuid().ToString(), null!, semanticValue: true, automationId: "nameBox");

        Assert.Equal("InvalidRequest", response.Error?.Code);
        fixture.UiAutomation.Verify(provider => provider.TrySetValue(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SemanticEntryBySelectorRefusesAnAmbiguousField()
    {
        var fixture = new Fixture(Snapshot(false,
            Element("el-9-1", WindowA, "Edit", "Name", "a"),
            Element("el-9-2", WindowB, "Edit", "Name", "b")));

        var response = await fixture.Tools.EnterTextAsync(
            SessionId, Guid.NewGuid().ToString(), "hello", semanticValue: true, role: "Edit", name: "Name");

        Assert.Equal("AmbiguousTarget", response.Error?.Code);
        fixture.UiAutomation.Verify(provider => provider.TrySetValue(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SemanticEntryByElementRefWithoutObservationUsesTheLatestObservedElement()
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.Observations.Setup(service => service.ResolveElement("el-1-2", ProcessRef))
            .Returns(new DesktopObservedElement("observation-1", Element("el-1-2", WindowA, "Edit", "Name", "nameBox")));
        fixture.UiAutomation.Setup(provider => provider.TrySetValue("el-1-2", "hello")).Returns(true);

        var response = await fixture.Tools.EnterTextAsync(
            SessionId, Guid.NewGuid().ToString(), "hello", semanticValue: true, elementRef: "el-1-2");

        Assert.Equal("Complete", response.Dispatch);
        Assert.Equal(0, fixture.InspectionCount);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("observation-1", null, 1, 1)]
    [InlineData("observation-1", "image-1", null, 1)]
    [InlineData(null, "image-1", 1, 1)]
    public async Task CoordinateTypingStillRequiresObservationImageAndPoint(string? observationRef, string? imageRef, int? x, int? y)
    {
        var fixture = new Fixture(Snapshot(false));

        var response = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", observationRef, imageRef, x, y);

        Assert.Equal("InvalidRequest", response.Error?.Code);
        fixture.Input.Verify(input => input.EnterTextAsync(It.IsAny<DesktopTextRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SelectorWithoutSemanticValueIsRejected()
    {
        var fixture = new Fixture(Snapshot(false, Element("el-9-1", WindowA, "Edit", "Name", "nameBox")));

        var response = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", automationId: "nameBox");

        Assert.Equal("InvalidRequest", response.Error?.Code);
        Assert.Equal(0, fixture.InspectionCount);
    }

    [Fact]
    public async Task SemanticEntryNeedsAFieldAndRejectsTwo()
    {
        var fixture = new Fixture(Snapshot(false));

        var none = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", semanticValue: true);
        var both = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", semanticValue: true, elementRef: "el-1", automationId: "nameBox");

        Assert.Equal("InvalidRequest", none.Error?.Code);
        Assert.Equal("InvalidRequest", both.Error?.Code);
    }

    [Fact]
    public async Task OldSemanticCallWithoutAnElementStillReportsElementRequired()
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.Observations.Setup(service => service.Resolve("observation-1")).Returns(Fixture.EmptyObservation());

        var response = await fixture.Tools.EnterTextAsync(
            SessionId, Guid.NewGuid().ToString(), "hello", "observation-1", "image-1", 0, 0, semanticValue: true);

        Assert.Equal("ElementRequired", response.Error?.Code);
    }

    [Fact]
    public async Task ObserveWithoutBoundsCapturesTheAppWindowsInPhysicalPixels()
    {
        var fixture = new Fixture(Snapshot(false));
        var secondMonitor = new PixelBounds(-1920, 100, 1280, 720);
        var primary = new PixelBounds(300, 200, 800, 600);
        fixture.WindowBounds.Setup(provider => provider.GetVisibleWindowBounds(1)).Returns([secondMonitor, primary]);
        DesktopObservationRequest? captured = null;
        fixture.Observations.Setup(service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DesktopObservationRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(Fixture.ObservationResult(Snapshot(false)));

        var result = await fixture.Tools.ObserveAppAsync(SessionId, includeImages: false);

        Assert.False(result.IsError);
        Assert.Equal([secondMonitor, primary], captured!.CaptureBoundsPixels);
    }

    [Fact]
    public async Task ObserveWithoutBoundsAndWithoutAWindowReportsNoVisibleWindow()
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.WindowBounds.Setup(provider => provider.GetVisibleWindowBounds(It.IsAny<int>())).Returns([]);

        var result = await fixture.Tools.ObserveAppAsync(SessionId);

        Assert.True(result.IsError);
        Assert.Equal("NoVisibleWindow", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        fixture.Observations.Verify(service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ObserveWithExplicitBoundsIsUnchanged()
    {
        var fixture = new Fixture(Snapshot(false));
        DesktopObservationRequest? captured = null;
        fixture.Observations.Setup(service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DesktopObservationRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(Fixture.ObservationResult(Snapshot(false)));

        await fixture.Tools.ObserveAppAsync(SessionId, [new McpPixelBounds(1, 2, 3, 4)], includeImages: false);

        Assert.Equal([new PixelBounds(1, 2, 3, 4)], captured!.CaptureBoundsPixels);
        fixture.WindowBounds.Verify(provider => provider.GetVisibleWindowBounds(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ObserveSelectorFilterReturnsOnlyMatchesAndTheirWindowWithCompletenessMetadata()
    {
        var fixture = new Fixture(Snapshot(false));
        var snapshot = Snapshot(true,
            Element("el-1", WindowA, "Window", "Main", null),
            Element("el-2", WindowA, "Button", "Save", "saveButton"),
            Element("el-3", WindowA, "Edit", "Name", "nameBox"),
            Element("el-4", WindowB, "Window", "Dialog", null),
            Element("el-5", WindowB, "Button", "Cancel", "cancelButton"));
        fixture.WindowBounds.Setup(provider => provider.GetVisibleWindowBounds(It.IsAny<int>())).Returns([new PixelBounds(0, 0, 100, 100)]);
        fixture.Observations.Setup(service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fixture.ObservationResult(snapshot));

        var result = await fixture.Tools.ObserveAppAsync(SessionId, includeImages: false, automationId: "saveButton");

        var structured = result.StructuredContent!.Value;
        var refs = structured.GetProperty("elements").EnumerateArray().Select(item => item.GetProperty("elementRef").GetString()).ToArray();
        Assert.Equal(["el-1", "el-2"], refs);
        Assert.True(structured.GetProperty("isTruncated").GetBoolean());
    }

    [Fact]
    public async Task ObserveWithoutFiltersReturnsEveryElement()
    {
        var fixture = new Fixture(Snapshot(false));
        var snapshot = Snapshot(false, Element("el-1", WindowA, "Window", "Main", null), Element("el-2", WindowA, "Button", "Save", "saveButton"));
        fixture.Observations.Setup(service => service.ObserveAsync(It.IsAny<DesktopObservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Fixture.ObservationResult(snapshot));

        var result = await fixture.Tools.ObserveAppAsync(SessionId, [new McpPixelBounds(0, 0, 10, 10)], includeImages: false);

        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("elements").GetArrayLength());
    }

    [Fact]
    public void WindowBoundsAreClippedToTheVirtualScreenAndCapped()
    {
        var virtualScreen = new PixelBounds(-1920, 0, 3840, 1080);
        var windows = new[]
        {
            new PixelBounds(-2000, 10, 400, 300),
            new PixelBounds(5000, 0, 100, 100),
            new PixelBounds(10, 10, 0, 50),
            new PixelBounds(-2000, 10, 400, 300),
            new PixelBounds(100, 100, 200, 200),
        };

        var result = DesktopWindowBounds.Normalize(windows, virtualScreen, 16);

        Assert.Equal([new PixelBounds(-1920, 10, 320, 300), new PixelBounds(100, 100, 200, 200)], result);
        Assert.Single(DesktopWindowBounds.Normalize(windows, virtualScreen, 1));
    }

    [Theory]
    [InlineData("Ctrl+S", new ushort[] { 0x11, 0x53 })]
    [InlineData("ctrl+shift+s", new ushort[] { 0x11, 0x10, 0x53 })]
    [InlineData("Shift+Ctrl+S", new ushort[] { 0x11, 0x10, 0x53 })]
    [InlineData("Alt+F4", new ushort[] { 0x12, 0x73 })]
    [InlineData("Win+D", new ushort[] { 0x5B, 0x44 })]
    [InlineData("F24", new ushort[] { 0x87 })]
    [InlineData("Enter", new ushort[] { 0x0D })]
    [InlineData("Esc", new ushort[] { 0x1B })]
    [InlineData("Tab", new ushort[] { 0x09 })]
    [InlineData("Space", new ushort[] { 0x20 })]
    [InlineData("Backspace", new ushort[] { 0x08 })]
    [InlineData("Delete", new ushort[] { 0x2E })]
    [InlineData("Ctrl+Home", new ushort[] { 0x11, 0x24 })]
    [InlineData("Ctrl+End", new ushort[] { 0x11, 0x23 })]
    [InlineData("PageUp", new ushort[] { 0x21 })]
    [InlineData("PageDown", new ushort[] { 0x22 })]
    [InlineData("Left", new ushort[] { 0x25 })]
    [InlineData("Up", new ushort[] { 0x26 })]
    [InlineData("Right", new ushort[] { 0x27 })]
    [InlineData("Down", new ushort[] { 0x28 })]
    [InlineData("Ctrl+7", new ushort[] { 0x11, 0x37 })]
    [InlineData("Ctrl+Shift+Alt+Z", new ushort[] { 0x11, 0x10, 0x12, 0x5A })]
    public void ValidChordsParseToVirtualKeys(string chord, ushort[] expected)
    {
        var parsed = DesktopKeyChordParser.Parse(chord);

        Assert.True(parsed.IsValid, parsed.ErrorMessage);
        Assert.Equal(expected, parsed.VirtualKeys);
    }

    [Theory]
    [InlineData("", "InvalidRequest")]
    [InlineData("Ctrl+", "InvalidRequest")]
    [InlineData("Ctrl", "InvalidRequest")]
    [InlineData("Ctrl+Ctrl+S", "InvalidRequest")]
    [InlineData("Ctrl+S+D", "InvalidRequest")]
    [InlineData("Ctrl+Banana", "InvalidRequest")]
    [InlineData("Ctrl+F25", "InvalidRequest")]
    [InlineData("Ctrl+F0", "InvalidRequest")]
    [InlineData("Ctrl+Shift+Alt+Win+S", "InvalidKeyCount")]
    public void InvalidChordsAreRejected(string chord, string code)
    {
        var parsed = DesktopKeyChordParser.Parse(chord);

        Assert.False(parsed.IsValid);
        Assert.Equal(code, parsed.ErrorCode);
    }

    [Fact]
    public async Task ChordPressDispatchesTheSameKeysAsVirtualKeys()
    {
        var fixture = new Fixture(Snapshot(false));
        DesktopKeyPressRequest? request = null;
        fixture.Input.Setup(input => input.PressKeysAsync(It.IsAny<DesktopKeyPressRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<DesktopKeyPressRequest, DesktopProcessIdentity, CancellationToken>((pressed, _, _) => request = pressed)
            .ReturnsAsync(DesktopInputPreflightResult.Valid());

        var response = await fixture.Tools.PressKeysAsync(SessionId, Guid.NewGuid().ToString(), windowRef: WindowA, chord: "Ctrl+S");

        Assert.Equal("Complete", response.Dispatch);
        Assert.Equal([(ushort)0x11, (ushort)0x53], request!.VirtualKeys);
        Assert.Equal(DesktopInputMethod.Physical, request.Method);
        Assert.Equal(WindowA, request.Target!.Window!.WindowRef);
    }

    [Fact]
    public async Task ChordAndVirtualKeysTogetherOrNeitherAreRejected()
    {
        var fixture = new Fixture(Snapshot(false));

        var both = await fixture.Tools.PressKeysAsync(SessionId, Guid.NewGuid().ToString(), [0x41], windowRef: WindowA, chord: "Ctrl+S");
        var neither = await fixture.Tools.PressKeysAsync(SessionId, Guid.NewGuid().ToString(), windowRef: WindowA);
        var tooMany = await fixture.Tools.PressKeysAsync(SessionId, Guid.NewGuid().ToString(), windowRef: WindowA, chord: "Ctrl+Shift+Alt+Win+S");

        Assert.Equal("InvalidRequest", both.Error?.Code);
        Assert.Equal("InvalidRequest", neither.Error?.Code);
        Assert.Equal("InvalidKeyCount", tooMany.Error?.Code);
        fixture.Input.Verify(input => input.PressKeysAsync(It.IsAny<DesktopKeyPressRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VirtualKeyPressesKeepTheirTargetRequirement()
    {
        var fixture = new Fixture(Snapshot(false));

        var response = await fixture.Tools.PressKeysAsync(SessionId, Guid.NewGuid().ToString(), [0x41]);

        Assert.Equal("TargetRequired", response.Error?.Code);
    }

    private static DesktopUiElementSnapshot At(string elementRef, PixelBounds bounds, string? name = "Save") =>
        new(elementRef, WindowA, "Button", name, "saveButton", bounds, true);

    [Fact]
    public async Task LayoutChangeAfterForegroundingClicksTheElementWhereItIsNow()
    {
        var fixture = new Fixture(Snapshot(false, At("el-1", new PixelBounds(100, 200, 40, 20))))
            .Then(Snapshot(false, At("el-2", new PixelBounds(300, 400, 60, 20))));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("Complete", response.Dispatch);
        fixture.Input.Verify(input => input.ClickAsync(
            It.Is<DesktopClickRequest>(click => click.X == 330 && click.Y == 410 && click.Target.BoundsPixels == new PixelBounds(300, 400, 60, 20)),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.HitTest.Verify(hit => hit.ElementOwnsPoint("el-2", 330, 410), Times.Once);
    }

    [Fact]
    public async Task ModalCoveringTheClickPointRefusesAndSendsNoClick()
    {
        var fixture = new Fixture(Snapshot(false, At("el-1", new PixelBounds(100, 200, 40, 20))));
        fixture.HitTest.Setup(hit => hit.ElementOwnsPoint(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns(false);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("TargetOccluded", response.Error?.Code);
        Assert.Equal("NotStarted", response.Dispatch);
        fixture.Input.Verify(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WithoutAHitTestProviderASelectorClickFailsClosed()
    {
        var fixture = new Fixture(Snapshot(false, At("el-1", new PixelBounds(100, 200, 40, 20))), noHitTest: true);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal("TargetOccluded", response.Error?.Code);
        fixture.Input.Verify(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("renamed")]
    [InlineData("duplicate")]
    [InlineData("incomplete")]
    public async Task ElementThatChangesAfterForegroundingIsRefused(string change)
    {
        var first = Snapshot(false, At("el-1", new PixelBounds(100, 200, 40, 20)));
        var second = change switch
        {
            "gone" => Snapshot(false),
            "renamed" => Snapshot(false, At("el-2", new PixelBounds(100, 200, 40, 20), name: "Other")),
            "duplicate" => Snapshot(false, At("el-2", new PixelBounds(100, 200, 40, 20)), At("el-3", new PixelBounds(300, 200, 40, 20))),
            _ => Snapshot(true, At("el-2", new PixelBounds(100, 200, 40, 20))),
        };
        var fixture = new Fixture(first).Then(second);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        Assert.Equal(change == "incomplete" ? "InspectionIncomplete" : "TargetChanged", response.Error?.Code);
        Assert.Equal("NotStarted", response.Dispatch);
        fixture.Input.Verify(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Fixture RealLedgerFixture(DesktopUiSnapshot snapshot, out DesktopTestReportService reports)
    {
        reports = new DesktopTestReportService();
        reports.Initialize(SessionId, "target.exe", "hash");
        return new Fixture(snapshot, new DesktopActionCoordinator(new DesktopActionLedger(), reports), reports);
    }

    [Fact]
    public async Task ReplayedSemanticSelectorEntryReturnsTheRecordedResultAfterTheObservationExpires()
    {
        var fixture = RealLedgerFixture(Snapshot(false, Element("el-9-1", WindowA, "Edit", "Name", "nameBox")), out _);
        fixture.UiAutomation.Setup(provider => provider.TrySetValue("el-9-1", "hello")).Returns(true);
        var actionId = Guid.NewGuid().ToString();
        fixture.Observations.Setup(service => service.Resolve("observation-1")).Returns(Fixture.EmptyObservation());

        var first = await fixture.Tools.EnterTextAsync(SessionId, actionId, "hello", "observation-1", semanticValue: true, automationId: "nameBox");
        fixture.Observations.Setup(service => service.Resolve("observation-1"))
            .Throws(new DesktopOperationException("StaleObservation", "expired"));
        var replay = await fixture.Tools.EnterTextAsync(SessionId, actionId, "hello", "observation-1", semanticValue: true, automationId: "nameBox");

        Assert.Equal("Complete", first.Dispatch);
        Assert.Equal("Complete", replay.Dispatch);
        Assert.Null(replay.Error);
        Assert.Equal(1, fixture.InspectionCount);
        fixture.UiAutomation.Verify(provider => provider.TrySetValue(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ReplayedSelectorInvokeKeepsItsMethodAndDoesNotInspectOrClickAgain()
    {
        var fixture = RealLedgerFixture(Snapshot(false, At("el-1", new PixelBounds(100, 200, 40, 20))), out _);
        var actionId = Guid.NewGuid().ToString();

        var first = await fixture.Tools.InvokeAsync(SessionId, actionId, automationId: "saveButton");
        var inspections = fixture.InspectionCount;
        var replay = await fixture.Tools.InvokeAsync(SessionId, actionId, automationId: "saveButton");

        Assert.Equal("click", first.Method);
        Assert.Equal("click", replay.Method);
        Assert.Equal(inspections, fixture.InspectionCount);
        fixture.Input.Verify(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SelectorStepsExportThroughTheRealReportServiceAndScopedOnesAreUnexportable()
    {
        var fixture = RealLedgerFixture(Snapshot(false,
            new DesktopUiElementSnapshot("el-1", WindowA, "Edit", "Name", "nameBox", new PixelBounds(0, 0, 0, 0), true),
            new DesktopUiElementSnapshot("el-2", WindowB, "Button", "OK", "okButton", new PixelBounds(0, 0, 0, 0), true)), out var reports);
        fixture.UiAutomation.Setup(provider => provider.TrySetValue("el-1", "hello")).Returns(true);
        fixture.UiAutomation.Setup(provider => provider.TryInvoke("el-2")).Returns(UiInvokeOutcome.Completed);

        await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", semanticValue: true, automationId: "nameBox");
        await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), role: "Button", name: "OK");
        await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "okButton", windowRef: WindowB);

        var entries = reports.GetScenarioRecording(SessionId).Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal("nameBox", entries[0].Step!.Value.GetProperty("enterText").GetProperty("automationId").GetString());
        Assert.Equal("hello", entries[0].Step!.Value.GetProperty("enterText").GetProperty("text").GetString());
        var invoke = entries[1].Step!.Value.GetProperty("invoke");
        Assert.Equal("Button", invoke.GetProperty("role").GetString());
        Assert.Equal("OK", invoke.GetProperty("name").GetString());
        Assert.Null(entries[2].Step);
        Assert.Equal("window refs are per session", entries[2].Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LegacyBlankObservationRefStillFailsAsStale(string blank)
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.Observations.Setup(service => service.Resolve(blank)).Throws(new DesktopOperationException("StaleObservation", "stale"));

        var coordinate = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", blank, "image-1", 1, 1);
        var semantic = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", blank, "image-1", 1, 1, semanticValue: true, elementRef: "el-1");

        Assert.Equal("StaleObservation", coordinate.Error?.Code);
        Assert.Equal("StaleObservation", semantic.Error?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LegacyBlankElementRefStillReportsElementRequired(string blank)
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.Observations.Setup(service => service.Resolve("observation-1")).Returns(Fixture.EmptyObservation());

        var invoke = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), blank);
        var semantic = await fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", "observation-1", "image-1", 0, 0, semanticValue: true, elementRef: blank);

        Assert.Equal("ElementRequired", invoke.Error?.Code);
        Assert.Equal("ElementRequired", semantic.Error?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LegacyBlankImageRefIsNotReportedAsInvalidRequest(string blank)
    {
        var fixture = new Fixture(Snapshot(false));
        fixture.Observations.Setup(service => service.Resolve("observation-1")).Returns(Fixture.EmptyObservation());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Tools.EnterTextAsync(SessionId, Guid.NewGuid().ToString(), "hello", "observation-1", blank, 1, 1));
    }

    [Fact]
    public async Task HungHitTestRefusesWithinTheDeadlineAbandonsTheWorkerAndClicksNothing()
    {
        var host = new HangingHitTestHost();
        await using var worker = new WorkerDesktopAutomationService(host, hitTestMilliseconds: 200);
        var fixture = new Fixture(Snapshot(false, At("el-1", new PixelBounds(100, 200, 40, 20))), hitTestOverride: worker);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), automationId: "saveButton");

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        Assert.Equal("TargetOccluded", response.Error?.Code);
        Assert.Equal("NotStarted", response.Dispatch);
        Assert.Equal(1, host.AbandonCalls);
        fixture.Input.Verify(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke(It.IsAny<string>()), Times.Never);
    }

    private sealed class HangingHitTestHost : IDesktopAutomationWorkerHost
    {
        public int AbandonCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<DesktopAutomationWorkerResponse> DispatchAsync(DesktopAutomationWorkerRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Operation == DesktopAutomationWorkerProtocol.Operations.HitTest)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new DesktopAutomationWorkerResponse(DesktopAutomationWorkerProtocol.Version, request.RequestId, true, "Ok");
        }

        public ValueTask AbandonAsync()
        {
            AbandonCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("desktop_invoke", new[] { "elementRef", "automationId", "role", "name", "windowRef" })]
    [InlineData("desktop_enter_text", new[] { "text", "observationRef", "imageRef", "x", "y", "semanticValue", "elementRef", "automationId", "role", "name", "windowRef" })]
    [InlineData("desktop_observe_app", new[] { "captureBoundsPixels", "automationId", "role", "name" })]
    [InlineData("desktop_press_keys", new[] { "virtualKeys", "chord", "windowRef" })]
    public void NewParametersAppearInTheToolSchemaAndAreOptional(string tool, string[] parameters)
    {
        var method = typeof(DesktopTestingMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);
        var schema = McpServerTool.Create(method, _ => null!).ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties");
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        foreach (var parameter in parameters)
        {
            Assert.True(properties.TryGetProperty(parameter, out _), $"{tool} schema lacks {parameter}.");
            if (parameter != "text")
            {
                Assert.DoesNotContain(parameter, required);
            }

            var description = method.GetParameters().Single(item => item.Name == parameter).GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description));
        }
    }

    private sealed class Fixture
    {
        private readonly DesktopUiSnapshot _snapshot;
        private readonly Queue<DesktopUiSnapshot> _later = new();

        public Fixture(DesktopUiSnapshot snapshot, IDesktopActionCoordinator? realCoordinator = null, IDesktopTestReportService? realReports = null, bool noHitTest = false, IWindowsUiAutomationHitTestProvider? hitTestOverride = null)
        {
            _snapshot = snapshot;
            var process = new DesktopProcessIdentity(ProcessRef, 1, DateTimeOffset.UtcNow, "target.exe", "hash");
            var sessions = new Mock<IDesktopTestSessionService>();
            sessions.Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DesktopTestSessionSnapshot(
                    SessionId,
                    "target",
                    DesktopSessionState.Active,
                    new DesktopTargetReference("target-1", "target", 1, process, default, LaunchedByDriver: true)));

            var coordinator = new Mock<IDesktopActionCoordinator>();
            coordinator.Setup(service => service.ExecuteAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<object?>(),
                    It.IsAny<Func<CancellationToken, Task<DesktopActionExecution>>>(),
                    It.IsAny<IReadOnlyList<string>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, string, object?, Func<CancellationToken, Task<DesktopActionExecution>>, IReadOnlyList<string>?, CancellationToken>(
                    async (_, actionId, _, dispatch, _, token) =>
                    {
                        var execution = await dispatch(token);
                        return new DesktopActionResult(
                            DesktopTestingLimits.SchemaVersion,
                            actionId,
                            DesktopOperationStatus.Completed,
                            execution.Dispatch,
                            execution.Verification,
                            execution.ObservationStatus,
                            execution.Error);
                    });

            var reports = new Mock<IDesktopTestReportService>();
            reports.Setup(service => service.IsActionUnannotated(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
            Input.Setup(service => service.FocusAsync(It.IsAny<DesktopInputTarget>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesktopInputPreflightResult.Valid());
            Input.Setup(service => service.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesktopInputPreflightResult.Valid());
            Input.Setup(service => service.TryReleaseOwnedInput()).Returns(true);
            Input.Setup(service => service.PressKeysAsync(It.IsAny<DesktopKeyPressRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesktopInputPreflightResult.Valid());
            Provider.Setup(provider => provider.Inspect(It.IsAny<DesktopObservationRequest>()))
                .Callback<DesktopObservationRequest>(_ => InspectionCount++)
                .Returns(() => InspectionCount > 1 && _later.Count > 0 ? _later.Dequeue() : _snapshot);
            HitTest.Setup(provider => provider.ElementOwnsPoint(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns(true);

            Tools = new DesktopTestingMcpTools(
                realCoordinator ?? coordinator.Object,
                sessions.Object,
                Observations.Object,
                Input.Object,
                UiAutomation.Object,
                new Mock<IDesktopUiCheckService>().Object,
                new Mock<IDesktopOcrObservationProvider>().Object,
                realReports ?? reports.Object,
                new Mock<IDesktopEvidenceRecorder>().Object,
                new DesktopTestingPolicyLoader(),
                new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null),
                Provider.Object,
                WindowBounds.Object,
                noHitTest ? null : hitTestOverride ?? HitTest.Object);
        }

        public Mock<IWindowsUiAutomationHitTestProvider> HitTest { get; } = new();

        // The second inspection returns this snapshot: the screen changed between resolving and clicking.
        public Fixture Then(DesktopUiSnapshot next)
        {
            _later.Enqueue(next);
            return this;
        }

        public Mock<IWindowsDesktopInputService> Input { get; } = new();

        public Mock<IWindowsUiAutomationActionProvider> UiAutomation { get; } = new();

        public Mock<IDesktopObservationService> Observations { get; } = new();

        public Mock<IDesktopUiObservationProvider> Provider { get; } = new();

        public Mock<IDesktopWindowBoundsProvider> WindowBounds { get; } = new();

        public DesktopTestingMcpTools Tools { get; }

        public int InspectionCount { get; private set; }

        public void AssertNothingSent()
        {
            Input.Verify(input => input.FocusAsync(It.IsAny<DesktopInputTarget>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
            Input.Verify(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
            UiAutomation.Verify(provider => provider.TryInvoke(It.IsAny<string>()), Times.Never);
            UiAutomation.Verify(provider => provider.TrySetValue(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        public static DesktopObservationResult EmptyObservation() => ObservationResult(new DesktopUiSnapshot(DesktopUiAutomationStatus.Available, [], DateTimeOffset.UtcNow));

        public static DesktopObservationResult ObservationResult(DesktopUiSnapshot snapshot)
        {
            var process = new DesktopProcessIdentity(ProcessRef, 1, DateTimeOffset.UtcNow, "target.exe", "hash");
            var observation = new DesktopObservation(
                DesktopTestingLimits.SchemaVersion,
                "observation-1",
                process,
                DesktopTargetState.Running,
                DesktopObservationStatus.Available,
                [new DesktopImageReference("image-1", 10, 10, new PixelBounds(0, 0, 10, 10), DateTimeOffset.UtcNow)],
                snapshot.Status,
                DateTimeOffset.UtcNow,
                snapshot.CapturedUtc,
                snapshot.IsTruncated);
            return new DesktopObservationResult(observation, snapshot, 1);
        }
    }
}
