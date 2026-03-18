using Microsoft.Extensions.Logging;

namespace SnippingTool.Services.Messaging;

public sealed class DefaultEventAggregator : IEventAggregator, IDisposable
{
    private readonly ILogger<DefaultEventAggregator> _logger;
    private readonly Dictionary<Type, List<IEventSubscription>> _subscriptions = [];
    private readonly object _sync = new();
    private bool _disposed;

    public DefaultEventAggregator(ILogger<DefaultEventAggregator> logger)
    {
        _logger = logger;
    }

    public IEventSubscription Subscribe<TEvent>(Func<TEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        var subscription = new EventSubscription<TEvent>(handler, Unsubscribe);
        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(subscription.EventType, out var subscriptions))
            {
                subscriptions = [];
                _subscriptions[subscription.EventType] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        _logger.LogDebug("Subscribed handler {SubscriptionId} to event {EventType}", subscription.Id, subscription.EventType.Name);
        return subscription;
    }

    public async ValueTask PublishAsync(object eventArgument)
    {
        ArgumentNullException.ThrowIfNull(eventArgument);
        ThrowIfDisposed();

        var eventType = eventArgument.GetType();
        List<IEventSubscription>? snapshot;
        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(eventType, out var subscriptions) || subscriptions.Count == 0)
            {
                return;
            }

            snapshot = [.. subscriptions];
        }

        List<Task>? pendingTasks = null;
        List<IEventSubscription>? deadSubscriptions = null;

        foreach (var subscription in snapshot)
        {
            ValueTask? task;
            try
            {
                task = subscription.TryInvoke(eventArgument);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invoking event handler for {EventType}", eventType.Name);
                continue;
            }

            if (!task.HasValue)
            {
                deadSubscriptions ??= [];
                deadSubscriptions.Add(subscription);
                continue;
            }

            try
            {
                if (task.Value.IsCompletedSuccessfully)
                {
                    task.Value.GetAwaiter().GetResult();
                }
                else
                {
                    pendingTasks ??= [];
                    pendingTasks.Add(SafeInvokeAsync(task.Value, eventType));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in event handler for {EventType}", eventType.Name);
            }
        }

        if (pendingTasks is not null)
        {
            await Task.WhenAll(pendingTasks).ConfigureAwait(false);
        }

        if (deadSubscriptions is not null)
        {
            lock (_sync)
            {
                if (_subscriptions.TryGetValue(eventType, out var subscriptions))
                {
                    foreach (var deadSubscription in deadSubscriptions)
                    {
                        subscriptions.RemoveAll(s => s.Id == deadSubscription.Id);
                    }

                    if (subscriptions.Count == 0)
                    {
                        _subscriptions.Remove(eventType);
                    }
                }
            }

            _logger.LogDebug("Pruned {Count} dead subscriptions for event {EventType}", deadSubscriptions.Count, eventType.Name);
        }
    }

    public void Unsubscribe(IEventSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (!_subscriptions.TryGetValue(subscription.EventType, out var subscriptions))
            {
                return;
            }

            subscriptions.RemoveAll(s => s.Id == subscription.Id);
            if (subscriptions.Count == 0)
            {
                _subscriptions.Remove(subscription.EventType);
            }
        }

        _logger.LogDebug("Unsubscribed handler {SubscriptionId} from event {EventType}", subscription.Id, subscription.EventType.Name);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _disposed = true;
            _subscriptions.Clear();
        }

        _logger.LogInformation("Event aggregator disposed and all subscriptions were cleared");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private async Task SafeInvokeAsync(ValueTask task, Type eventType)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in asynchronous event handler for {EventType}", eventType.Name);
        }
    }
}
