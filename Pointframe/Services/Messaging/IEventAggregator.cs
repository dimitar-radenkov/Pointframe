namespace Pointframe.Services.Messaging;

public interface IEventAggregator
{
    IEventSubscription Subscribe<TEvent>(Func<TEvent, ValueTask> handler);

    ValueTask Publish(object eventArgument);

    void Unsubscribe(IEventSubscription subscription);
}
