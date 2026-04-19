using System.Reflection;

namespace Pointframe.Services.Messaging;

internal sealed class EventSubscription<TEvent> : IEventSubscription
{
    private readonly Action<IEventSubscription> _unsubscribe;
    private readonly MethodInfo _method;
    private readonly Func<TEvent, ValueTask>? _staticHandler;
    private readonly WeakReference<object>? _targetReference;
    private bool _disposed;

    public EventSubscription(Func<TEvent, ValueTask> handler, Action<IEventSubscription> unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        _unsubscribe = unsubscribe;
        _method = handler.Method;
        EventType = typeof(TEvent);
        Id = Guid.NewGuid().ToString("N");

        if (handler.Target is null)
        {
            _staticHandler = handler;
        }
        else
        {
            _targetReference = new WeakReference<object>(handler.Target);
        }
    }

    public string Id { get; }

    public Type EventType { get; }

    public bool IsAlive => _staticHandler is not null || _targetReference?.TryGetTarget(out _) == true;

    public ValueTask? TryInvoke(object eventArgument)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (eventArgument is not TEvent typedEvent)
        {
            throw new ArgumentException($"Expected event argument of type {typeof(TEvent).FullName}.", nameof(eventArgument));
        }

        if (_staticHandler is not null)
        {
            return _staticHandler(typedEvent);
        }

        if (_targetReference is null || !_targetReference.TryGetTarget(out var target))
        {
            return null;
        }

        var instanceHandler = (Func<TEvent, ValueTask>)_method.CreateDelegate(typeof(Func<TEvent, ValueTask>), target);
        return instanceHandler(typedEvent);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _unsubscribe(this);
    }
}
