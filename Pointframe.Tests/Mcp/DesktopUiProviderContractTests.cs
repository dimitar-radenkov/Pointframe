using System.IO;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopUiProviderContractTests
{
    [Fact]
    public void UiAutomationProvider_WithoutBackendIsUnavailable()
    {
        var provider = new WindowsUiAutomationProvider();
        var result = provider.Inspect(new DesktopObservationRequest(
            new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash"),
            [new PixelBounds(0, 0, 10, 10)]));

        Assert.Equal(DesktopUiAutomationStatus.Unavailable, result.Status);
        Assert.Empty(result.Elements);
    }

    [Fact]
    public void UiAutomationProvider_DelegatesExplicitInvokeAndValueActions()
    {
        var backend = new FakeUiBackend();
        var provider = new WindowsUiAutomationProvider(backend);

        Assert.True(provider.TryInvoke("element-1"));
        Assert.True(provider.TrySetValue("element-1", "λ"));
        Assert.Equal("element-1", backend.InvokedElement);
        Assert.Equal(("element-1", "λ"), backend.SetValueRequest);
    }

    [Fact]
    public void UiAutomationProvider_RejectsSemanticActionsWhenBackendDoesNotSupportThem()
    {
        var provider = new WindowsUiAutomationProvider(new FakeInspectionBackend());

        Assert.False(provider.TryInvoke("element-1"));
        Assert.False(provider.TrySetValue("element-1", "value"));
    }

    [Fact]
    public void ShellProvider_RejectsUnsupportedSurface()
    {
        var exception = Assert.Throws<DesktopOperationException>(() =>
            new WindowsShellSurfaceProvider().ResolveSurface((DesktopSurfaceKind)999));

        Assert.Equal("UnsupportedSurface", exception.Code);
    }

    [Fact]
    public void ShellProvider_DoesNotTrustUnknownOwner()
    {
        var provider = new WindowsShellSurfaceProvider(new FakeShellBackend());

        Assert.Null(provider.ResolveSurface(DesktopSurfaceKind.NotificationArea));
    }

    [Fact]
    public void ShellProvider_TrustsExplorerAndShellExperienceHostOwners()
    {
        var provider = new WindowsShellSurfaceProvider(new TrustedShellBackend());

        var area = provider.ResolveSurface(DesktopSurfaceKind.NotificationArea);
        var overflow = provider.ResolveSurface(DesktopSurfaceKind.NotificationOverflow);

        Assert.NotNull(area);
        Assert.NotNull(overflow);
        Assert.Equal("explorer.exe", Path.GetFileName(area!.Identity.OwnerProcessRef), ignoreCase: true);
        Assert.Equal("ShellExperienceHost.exe", Path.GetFileName(overflow!.Identity.OwnerProcessRef), ignoreCase: true);
    }

    private sealed class FakeShellBackend : IWindowsShellSurfaceBackend
    {
        public IReadOnlyList<WindowsShellSurface> GetApprovedSurfaces()
        {
            return
            [
                new WindowsShellSurface(
                    new DesktopSurfaceIdentity("surface-1", DesktopSurfaceKind.NotificationArea, "unknown-process"),
                    new PixelBounds(0, 0, 20, 20)),
            ];
        }
    }

    private sealed class TrustedShellBackend : IWindowsShellSurfaceBackend
    {
        public IReadOnlyList<WindowsShellSurface> GetApprovedSurfaces()
        {
            return
            [
                new WindowsShellSurface(
                    new DesktopSurfaceIdentity("surface-1", DesktopSurfaceKind.NotificationArea, "C:\\Windows\\explorer.exe"),
                    new PixelBounds(0, 0, 20, 20)),
                new WindowsShellSurface(
                    new DesktopSurfaceIdentity("surface-2", DesktopSurfaceKind.NotificationOverflow, "C:\\Windows\\SystemApps\\ShellExperienceHost\\ShellExperienceHost.exe"),
                    new PixelBounds(0, 0, 30, 30)),
            ];
        }
    }

    private sealed class FakeUiBackend : FakeInspectionBackend, IWindowsUiAutomationActionBackend
    {
        public string? InvokedElement { get; private set; }

        public (string ElementRef, string Value)? SetValueRequest { get; private set; }

        public bool TryInvoke(string elementRef)
        {
            InvokedElement = elementRef;
            return true;
        }

        public bool TrySetValue(string elementRef, string value)
        {
            SetValueRequest = (elementRef, value);
            return true;
        }
    }

    private class FakeInspectionBackend : IWindowsUiAutomationBackend
    {
        public WindowsUiAutomationInspection Inspect(DesktopObservationRequest request) =>
            new(DesktopUiAutomationStatus.Available, [], DateTimeOffset.UtcNow);

        public DesktopUiElementSnapshot? ResolveLocator(DesktopLocator locator) => null;

        public DesktopUiElementSnapshot? ResolveElement(string elementRef) => null;
    }
}
