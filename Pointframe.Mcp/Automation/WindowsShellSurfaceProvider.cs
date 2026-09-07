using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp.Automation;

public sealed record WindowsShellSurface(
    DesktopSurfaceIdentity Identity,
    PixelBounds BoundsPixels,
    string? WindowRef = null);

public interface IWindowsShellSurfaceBackend
{
    IReadOnlyList<WindowsShellSurface> GetApprovedSurfaces();
}

public sealed class WindowsShellSurfaceProvider
{
    private readonly IWindowsShellSurfaceBackend? _backend;

    public WindowsShellSurfaceProvider(IWindowsShellSurfaceBackend? backend = null)
    {
        _backend = backend;
    }

    public WindowsShellSurface? ResolveSurface(DesktopSurfaceKind kind)
    {
        if (kind is not (DesktopSurfaceKind.NotificationArea or DesktopSurfaceKind.NotificationOverflow))
        {
            throw new DesktopOperationException("UnsupportedSurface", "Only approved notification surfaces are supported.");
        }

        return _backend?.GetApprovedSurfaces()
            .Where(surface => surface.Identity.Kind == kind)
            .Where(surface => IsApprovedOwner(surface.Identity.OwnerProcessRef))
            .SingleOrDefault();
    }

    public bool ValidateMenuRelationship(WindowsShellSurface parent, WindowsShellSurface child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        return parent.Identity.Kind == DesktopSurfaceKind.NotificationArea
            && child.Identity.Kind == DesktopSurfaceKind.NotificationOverflow
            && IsApprovedOwner(parent.Identity.OwnerProcessRef)
            && IsApprovedOwner(child.Identity.OwnerProcessRef)
            && string.Equals(parent.Identity.OwnerProcessRef, child.Identity.OwnerProcessRef, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovedOwner(string ownerProcessRef)
    {
        return ownerProcessRef.Contains("explorer", StringComparison.OrdinalIgnoreCase)
            || ownerProcessRef.Contains("shell", StringComparison.OrdinalIgnoreCase);
    }
}
