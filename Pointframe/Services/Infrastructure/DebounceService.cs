using System.Collections.Concurrent;

namespace Pointframe.Services;

public sealed class DebounceService : IDebounceService, IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new();

    public async Task DebounceAsync(
        string key,
        TimeSpan delay,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);

        var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pending.AddOrUpdate(
            key,
            _ => current,
            (_, existing) =>
            {
                existing.Cancel();
                existing.Dispose();
                return current;
            });

        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, current.Token);
            }

            if (!_pending.TryGetValue(key, out var active) || !ReferenceEquals(active, current))
            {
                return;
            }

            await action(current.Token);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            // A newer call superseded this invocation.
        }
        finally
        {
            if (_pending.TryGetValue(key, out var active) && ReferenceEquals(active, current))
            {
                _pending.TryRemove(key, out _);
            }

            current.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var pending in _pending.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }

        _pending.Clear();
    }
}

internal sealed class ImmediateDebounceService : IDebounceService
{
    public static ImmediateDebounceService Instance { get; } = new();

    private ImmediateDebounceService()
    {
    }

    public Task DebounceAsync(
        string key,
        TimeSpan delay,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(action);
        return action(cancellationToken);
    }
}
