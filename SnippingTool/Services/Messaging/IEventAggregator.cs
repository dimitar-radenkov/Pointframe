namespace SnippingTool.Services.Messaging;

public interface IEventAggregator
{
    IEventSubscription Subscribe<TEvent>(Func<TEvent, ValueTask> handler);

    ValueTask PublishAsync(object eventArgument);

    void Unsubscribe(IEventSubscription subscription);
}
