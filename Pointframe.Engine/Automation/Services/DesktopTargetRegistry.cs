using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopTargetRegistry
{
    bool TryGet(string targetRef, out DesktopTargetReference? target);

    void Add(DesktopTargetReference target);

    void Update(DesktopTargetReference target);

    bool Remove(string targetRef);
}

public sealed class DesktopTargetRegistry : IDesktopTargetRegistry
{
    private readonly Dictionary<string, DesktopTargetReference> _targets = new(StringComparer.Ordinal);

    public bool TryGet(string targetRef, out DesktopTargetReference? target)
    {
        return _targets.TryGetValue(targetRef, out target);
    }

    public void Add(DesktopTargetReference target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_targets.TryAdd(target.TargetRef, target))
        {
            throw new InvalidOperationException($"The target reference already exists: {target.TargetRef}");
        }
    }

    public void Update(DesktopTargetReference target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_targets.ContainsKey(target.TargetRef))
        {
            throw new KeyNotFoundException($"The target reference was not found: {target.TargetRef}");
        }

        _targets[target.TargetRef] = target;
    }

    public bool Remove(string targetRef)
    {
        return _targets.Remove(targetRef);
    }
}
