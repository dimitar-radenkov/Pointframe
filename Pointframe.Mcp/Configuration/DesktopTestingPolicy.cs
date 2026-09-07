using System.Collections.ObjectModel;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Configuration;

public sealed record DesktopTestingPolicy(
    int SchemaVersion,
    string ArtifactRoot,
    DesktopEvidencePolicy EvidencePolicy,
    IReadOnlyList<DesktopTestingProfile> Profiles);

public enum DesktopEvidencePolicy
{
    None,
    Failures,
    All,
}

public sealed record DesktopTestingProfile(
    string Id,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool AllowAttach,
    IReadOnlySet<DesktopTestingAction> AllowedActions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedGlobalHotkeys,
    IReadOnlySet<DesktopSurfaceKind> AllowedShellSurfaces,
    bool AllowMonitorObservation)
{
    public DesktopTestingProfile(
        string id,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool allowAttach,
        IEnumerable<DesktopTestingAction> allowedActions,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> allowedGlobalHotkeys,
        IEnumerable<DesktopSurfaceKind> allowedShellSurfaces,
        bool allowMonitorObservation)
        : this(
            id,
            executablePath,
            new ReadOnlyCollection<string>(arguments.ToArray()),
            workingDirectory,
            allowAttach,
            new ReadOnlySet<DesktopTestingAction>(allowedActions),
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                allowedGlobalHotkeys.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)new ReadOnlyCollection<string>(pair.Value.ToArray()),
                    StringComparer.OrdinalIgnoreCase)),
            new ReadOnlySet<DesktopSurfaceKind>(allowedShellSurfaces),
            allowMonitorObservation)
    {
    }
}

internal sealed class ReadOnlySet<T>(IEnumerable<T> values) : ReadOnlyCollection<T>(values.Distinct().ToList()), IReadOnlySet<T>
    where T : notnull
{
    public bool IsProperSubsetOf(IEnumerable<T> other)
    {
        return Items.ToHashSet().IsProperSubsetOf(other);
    }

    public bool IsProperSupersetOf(IEnumerable<T> other)
    {
        return Items.ToHashSet().IsProperSupersetOf(other);
    }

    public bool IsSubsetOf(IEnumerable<T> other)
    {
        return Items.ToHashSet().IsSubsetOf(other);
    }

    public bool IsSupersetOf(IEnumerable<T> other)
    {
        return Items.ToHashSet().IsSupersetOf(other);
    }

    public bool Overlaps(IEnumerable<T> other)
    {
        return Items.ToHashSet().Overlaps(other);
    }

    public bool SetEquals(IEnumerable<T> other)
    {
        return Items.ToHashSet().SetEquals(other);
    }
}
