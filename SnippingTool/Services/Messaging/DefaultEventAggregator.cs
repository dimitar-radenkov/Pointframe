using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace Pointframe.Services.Messaging;

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

    public async ValueTask Publish(object eventArgument)
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
        List<Exception>? failures = null;

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
                failures ??= [];
                failures.Add(ex);
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
                    continue;
                }
                pendingTasks ??= [];
                pendingTasks.Add(task.Value.AsTask());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in event handler for {EventType}", eventType.Name);
                failures ??= [];
                failures.Add(ex);
            }
        }

        if (pendingTasks is not null)
        {
            try
            {
                await Task.WhenAll(pendingTasks).ConfigureAwait(false);
            }
            catch
            {
                failures ??= [];
                foreach (var task in pendingTasks)
                {
                    if (task.Exception is not null)
                    {
                        foreach (var exception in task.Exception.InnerExceptions)
                        {
                            _logger.LogError(exception, "Error in asynchronous event handler for {EventType}", eventType.Name);
                            failures.Add(exception);
                        }
                    }
                    else if (task.IsCanceled)
                    {
                        var exception = new TaskCanceledException(task);
                        _logger.LogError(exception, "Event handler for {EventType} was cancelled", eventType.Name);
                        failures.Add(exception);
                    }
                }
            }
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

        if (failures is { Count: > 0 })
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            throw new AggregateException($"One or more handlers failed while publishing {eventType.Name}.", failures);
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
}
