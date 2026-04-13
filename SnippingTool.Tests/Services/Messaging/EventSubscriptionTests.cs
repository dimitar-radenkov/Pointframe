using SnippingTool.Services.Messaging;
using Xunit;

namespace SnippingTool.Tests.Services.Messaging;

public sealed class EventSubscriptionTests
{
    private static readonly Action<IEventSubscription> NoopUnsubscribe = _ => { };

    private sealed record TestEvent(string Value);

    // ── Static handler ───────────────────────────────────────────────────────

    [Fact]
    public async Task TryInvoke_StaticHandler_InvokesHandlerAndReturnsValue()
    {
        TestEvent? received = null;

        ValueTask StaticHandler(TestEvent e)
        {
            received = e;
            return ValueTask.CompletedTask;
        }

        var subscription = new EventSubscription<TestEvent>(StaticHandler, NoopUnsubscribe);
        var result = subscription.TryInvoke(new TestEvent("hello"));

        Assert.True(result.HasValue);
        await result!.Value;
        Assert.Equal("hello", received?.Value);
    }

    [Fact]
    public void IsAlive_StaticHandler_ReturnsTrue()
    {
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var subscription = new EventSubscription<TestEvent>(Handler, NoopUnsubscribe);

        Assert.True(subscription.IsAlive);
    }

    // ── Instance handler ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryInvoke_InstanceHandler_InvokesHandlerViaWeakReference()
    {
        var target = new HandlerHolder();
        var subscription = new EventSubscription<TestEvent>(target.Handle, NoopUnsubscribe);

        var result = subscription.TryInvoke(new TestEvent("world"));

        Assert.True(result.HasValue);
        await result!.Value;
        Assert.Equal("world", target.LastReceived?.Value);
    }

    [Fact]
    public void IsAlive_LiveInstanceHandler_ReturnsTrue()
    {
        var target = new HandlerHolder();
        var subscription = new EventSubscription<TestEvent>(target.Handle, NoopUnsubscribe);

        Assert.True(subscription.IsAlive);

        GC.KeepAlive(target);
    }

    // ── Dead instance handler (GC'd target) ──────────────────────────────────

    [Fact]
    public void TryInvoke_DeadInstanceHandler_ReturnsNull()
    {
        var subscription = CreateSubscriptionWithDeadTarget();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var result = subscription.TryInvoke(new TestEvent("x"));

        Assert.Null(result);
    }

    [Fact]
    public void IsAlive_DeadInstanceHandler_ReturnsFalse()
    {
        var subscription = CreateSubscriptionWithDeadTarget();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(subscription.IsAlive);
    }

    // Separate method so the target goes out of scope and becomes GC-eligible.
    private static EventSubscription<TestEvent> CreateSubscriptionWithDeadTarget()
    {
        var target = new HandlerHolder();
        return new EventSubscription<TestEvent>(target.Handle, NoopUnsubscribe);
        // target is not kept alive after this method returns.
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryInvoke_AfterDispose_ThrowsObjectDisposedException()
    {
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var subscription = new EventSubscription<TestEvent>(Handler, NoopUnsubscribe);

        subscription.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            subscription.TryInvoke(new TestEvent("x")));
    }

    [Fact]
    public void Dispose_CallsUnsubscribeCallbackExactlyOnce()
    {
        var callCount = 0;
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var subscription = new EventSubscription<TestEvent>(Handler, _ => callCount++);

        subscription.Dispose();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Dispose_CalledTwice_UnsubscribeCallbackCalledOnlyOnce()
    {
        var callCount = 0;
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var subscription = new EventSubscription<TestEvent>(Handler, _ => callCount++);

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(1, callCount);
    }

    // ── Argument validation ──────────────────────────────────────────────────

    [Fact]
    public void TryInvoke_WrongEventType_ThrowsArgumentException()
    {
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var subscription = new EventSubscription<TestEvent>(Handler, NoopUnsubscribe);

        Assert.Throws<ArgumentException>(() =>
            subscription.TryInvoke("not-a-TestEvent"));
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    [Fact]
    public void Id_IsUniquePerSubscription()
    {
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var a = new EventSubscription<TestEvent>(Handler, NoopUnsubscribe);
        var b = new EventSubscription<TestEvent>(Handler, NoopUnsubscribe);

        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void EventType_MatchesGenericParameter()
    {
        static ValueTask Handler(TestEvent _) => ValueTask.CompletedTask;
        var subscription = new EventSubscription<TestEvent>(Handler, NoopUnsubscribe);

        Assert.Equal(typeof(TestEvent), subscription.EventType);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class HandlerHolder
    {
        public TestEvent? LastReceived { get; private set; }

        public ValueTask Handle(TestEvent e)
        {
            LastReceived = e;
            return ValueTask.CompletedTask;
        }
    }
}
