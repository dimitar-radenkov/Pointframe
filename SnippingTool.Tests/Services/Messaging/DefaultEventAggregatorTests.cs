using Microsoft.Extensions.Logging.Abstractions;
using SnippingTool.Services.Messaging;
using Xunit;

namespace SnippingTool.Tests.Services.Messaging;

public sealed class DefaultEventAggregatorTests
{
    private static DefaultEventAggregator Create() =>
        new(NullLogger<DefaultEventAggregator>.Instance);

    private sealed record TestEvent(string Value);

    private sealed record OtherEvent(int Value);

    // ── Subscribe / Publish ──────────────────────────────────────────────────

    [Fact]
    public async Task Publish_SingleSubscriber_HandlerInvoked()
    {
        var sut = Create();
        TestEvent? received = null;
        sut.Subscribe<TestEvent>(e =>
        {
            received = e;
            return ValueTask.CompletedTask;
        });

        await sut.Publish(new TestEvent("hello"));

        Assert.NotNull(received);
        Assert.Equal("hello", received.Value);
    }

    [Fact]
    public async Task Publish_MultipleSubscribersSameType_AllHandlersInvoked()
    {
        var sut = Create();
        var calls = new List<string>();
        sut.Subscribe<TestEvent>(e => { calls.Add("first"); return ValueTask.CompletedTask; });
        sut.Subscribe<TestEvent>(e => { calls.Add("second"); return ValueTask.CompletedTask; });

        await sut.Publish(new TestEvent("x"));

        Assert.Equal(["first", "second"], calls);
    }

    [Fact]
    public async Task Publish_DifferentEventTypes_DoNotCrossfire()
    {
        var sut = Create();
        var testCalls = 0;
        var otherCalls = 0;
        sut.Subscribe<TestEvent>(_ => { testCalls++; return ValueTask.CompletedTask; });
        sut.Subscribe<OtherEvent>(_ => { otherCalls++; return ValueTask.CompletedTask; });

        await sut.Publish(new TestEvent("a"));

        Assert.Equal(1, testCalls);
        Assert.Equal(0, otherCalls);
    }

    [Fact]
    public async Task Publish_NoSubscribers_CompletesWithoutError()
    {
        var sut = Create();

        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent("x")).AsTask());

        Assert.Null(ex);
    }

    // ── Argument validation ──────────────────────────────────────────────────

    [Fact]
    public void Subscribe_NullHandler_ThrowsArgumentNullException()
    {
        var sut = Create();

        Assert.Throws<ArgumentNullException>(() =>
            sut.Subscribe<TestEvent>(null!));
    }

    [Fact]
    public async Task Publish_NullArgument_ThrowsArgumentNullException()
    {
        var sut = Create();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.Publish(null!).AsTask());
    }

    // ── Unsubscribe ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Unsubscribe_ViaReturnedToken_HandlerNoLongerInvoked()
    {
        var sut = Create();
        var calls = 0;
        var token = sut.Subscribe<TestEvent>(_ => { calls++; return ValueTask.CompletedTask; });

        sut.Unsubscribe(token);
        await sut.Publish(new TestEvent("after"));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Unsubscribe_ViaDispose_HandlerNoLongerInvoked()
    {
        var sut = Create();
        var calls = 0;
        var token = sut.Subscribe<TestEvent>(_ => { calls++; return ValueTask.CompletedTask; });

        token.Dispose();
        await sut.Publish(new TestEvent("after"));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Unsubscribe_OneOfTwoHandlers_OtherHandlerStillInvoked()
    {
        var sut = Create();
        var firstCalls = 0;
        var secondCalls = 0;
        var first = sut.Subscribe<TestEvent>(_ => { firstCalls++; return ValueTask.CompletedTask; });
        sut.Subscribe<TestEvent>(_ => { secondCalls++; return ValueTask.CompletedTask; });

        first.Dispose();
        await sut.Publish(new TestEvent("x"));

        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public void Unsubscribe_NullSubscription_ThrowsArgumentNullException()
    {
        var sut = Create();

        Assert.Throws<ArgumentNullException>(() => sut.Unsubscribe(null!));
    }

    // ── Exception propagation ────────────────────────────────────────────────

    [Fact]
    public async Task Publish_SingleHandlerThrows_ExceptionReThrownDirectly()
    {
        var sut = Create();
        var expected = new InvalidOperationException("boom");
        sut.Subscribe<TestEvent>(_ => throw expected);

        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent("x")).AsTask());

        Assert.Same(expected, ex);
    }

    [Fact]
    public async Task Publish_MultipleHandlersBothThrow_AggregateExceptionContainsBoth()
    {
        var sut = Create();
        sut.Subscribe<TestEvent>(_ => throw new InvalidOperationException("first"));
        sut.Subscribe<TestEvent>(_ => throw new InvalidOperationException("second"));

        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent("x")).AsTask());

        var agg = Assert.IsType<AggregateException>(ex);
        Assert.Equal(2, agg.InnerExceptions.Count);
    }

    [Fact]
    public async Task Publish_AsyncHandlerThrows_ExceptionPropagated()
    {
        var sut = Create();
        sut.Subscribe<TestEvent>(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("async-boom");
        });

        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent("x")).AsTask());

        Assert.NotNull(ex);
        Assert.Equal("async-boom", ex!.Message);
    }

    // ── Async handler awaiting ───────────────────────────────────────────────

    [Fact]
    public async Task Publish_AsyncHandler_CompletesBeforePublishReturns()
    {
        var sut = Create();
        var tcs = new TaskCompletionSource();
        var handlerCompleted = false;

        sut.Subscribe<TestEvent>(async _ =>
        {
            await tcs.Task;
            handlerCompleted = true;
        });

        var publishTask = sut.Publish(new TestEvent("x")).AsTask();
        Assert.False(handlerCompleted);

        tcs.SetResult();
        await publishTask;

        Assert.True(handlerCompleted);
    }

    // ── Dead subscription pruning ────────────────────────────────────────────

    [Fact]
    public async Task Publish_SubscriberTargetCollected_DeadSubscriptionPrunedSilently()
    {
        var sut = Create();
        WeakSubscribeAndForget(sut);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Should complete without error even though the handler target is gone.
        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent("after-gc")).AsTask());

        Assert.Null(ex);
    }

    // Separate method so the closure's target object is eligible for GC once it returns.
    private static void WeakSubscribeAndForget(DefaultEventAggregator sut)
    {
        var handler = new HandlerHolder();
        sut.Subscribe<TestEvent>(handler.Handle);
        // handler goes out of scope here — the subscription holds only a WeakReference to it.
    }

    private sealed class HandlerHolder
    {
        public ValueTask Handle(TestEvent _) => ValueTask.CompletedTask;
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void Subscribe_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = Create();
        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            sut.Subscribe<TestEvent>(_ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task Publish_AfterDispose_ThrowsObjectDisposedException()
    {
        var sut = Create();
        sut.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sut.Publish(new TestEvent("x")).AsTask());
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var sut = Create();
        sut.Dispose();

        var ex = Record.Exception(() => sut.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void Unsubscribe_AfterDispose_DoesNotThrow()
    {
        var sut = Create();
        var token = sut.Subscribe<TestEvent>(_ => ValueTask.CompletedTask);
        sut.Dispose();

        // Unsubscribe after dispose is explicitly allowed and must be a no-op.
        var ex = Record.Exception(() => sut.Unsubscribe(token));

        Assert.Null(ex);
    }
}
