namespace SnippingTool.Services.Messaging;

public interface IEventSubscription : IDisposable
{
    string Id { get; }

    Type EventType { get; }

    bool IsAlive { get; }

    ValueTask? TryInvoke(object eventArgument);
}
