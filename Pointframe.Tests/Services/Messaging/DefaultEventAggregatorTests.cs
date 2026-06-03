using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services.Messaging;
using Xunit;

namespace Pointframe.Tests.Services.Messaging;

public sealed class DefaultEventAggregatorTests
{
    private record TestEvent(int Value = 0);
    private record OtherEvent;

    private sealed class HandlerHost
    {
        public int CallCount;
        public ValueTask Handle(TestEvent _) { CallCount++; return ValueTask.CompletedTask; }
    }

    private static DefaultEventAggregator CreateSut()
        => new(NullLogger<DefaultEventAggregator>.Instance);

    // ── Null / disposed guards ───────────────────────────────────────────────

    [Fact]
    public void Subscribe_NullHandler_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateSut();

        // Act / Assert
        Assert.Throws<ArgumentNullException>(() =>
            sut.Subscribe<TestEvent>(null!));
    }

    [Fact]
    public void Subscribe_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var sut = CreateSut();
        sut.Dispose();

        // Act / Assert
        Assert.Throws<ObjectDisposedException>(() =>
            sut.Subscribe<TestEvent>(_ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task Publish_NullArgument_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateSut();

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.Publish(null!).AsTask());
    }

    [Fact]
    public async Task Publish_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var sut = CreateSut();
        sut.Dispose();

        // Act / Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            sut.Publish(new TestEvent()).AsTask());
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_NoSubscribers_CompletesWithoutError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task Publish_WithSubscriber_InvokesHandler()
    {
        // Arrange
        var sut = CreateSut();
        var called = false;
        sut.Subscribe<TestEvent>(_ => { called = true; return ValueTask.CompletedTask; });

        // Act
        await sut.Publish(new TestEvent());

        // Assert
        Assert.True(called);
    }

    [Fact]
    public async Task Publish_HandlerReceivesCorrectEventInstance()
    {
        // Arrange
        var sut = CreateSut();
        TestEvent? received = null;
        sut.Subscribe<TestEvent>(e => { received = e; return ValueTask.CompletedTask; });
        var published = new TestEvent(Value: 42);

        // Act
        await sut.Publish(published);

        // Assert
        Assert.Same(published, received);
    }

    [Fact]
    public async Task Publish_MultipleSubscribers_AllInvoked()
    {
        // Arrange
        var sut = CreateSut();
        var invoked = new List<int>();
        sut.Subscribe<TestEvent>(_ => { invoked.Add(1); return ValueTask.CompletedTask; });
        sut.Subscribe<TestEvent>(_ => { invoked.Add(2); return ValueTask.CompletedTask; });
        sut.Subscribe<TestEvent>(_ => { invoked.Add(3); return ValueTask.CompletedTask; });

        // Act
        await sut.Publish(new TestEvent());

        // Assert
        Assert.Equal([1, 2, 3], invoked);
    }

    [Fact]
    public async Task Publish_DifferentEventTypes_DoNotInterfere()
    {
        // Arrange
        var sut = CreateSut();
        var testEventCount = 0;
        var otherEventCount = 0;
        sut.Subscribe<TestEvent>(_ => { testEventCount++; return ValueTask.CompletedTask; });
        sut.Subscribe<OtherEvent>(_ => { otherEventCount++; return ValueTask.CompletedTask; });

        // Act
        await sut.Publish(new TestEvent());

        // Assert
        Assert.Equal(1, testEventCount);
        Assert.Equal(0, otherEventCount);
    }

    // ── Async handler execution ──────────────────────────────────────────────

    [Fact]
    public async Task Publish_AsyncHandler_IsAwaited()
    {
        // Arrange
        var sut = CreateSut();
        var tcs = new TaskCompletionSource();
        var completed = false;
        sut.Subscribe<TestEvent>(async _ =>
        {
            await tcs.Task;
            completed = true;
        });

        // Act
        var publishTask = sut.Publish(new TestEvent()).AsTask();

        // handler is suspended waiting for the gate — Publish has not completed
        Assert.False(completed);
        tcs.SetResult();
        await publishTask;

        // Assert
        Assert.True(completed);
    }

    // ── Failure paths ────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_HandlerThrowsSynchronously_ExceptionIsPropagated()
    {
        // Arrange
        var sut = CreateSut();
        var expected = new InvalidOperationException("boom");
        sut.Subscribe<TestEvent>(_ => throw expected);

        // Act
        var actual = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Publish_TwoHandlersThrowSynchronously_ThrowsAggregateException()
    {
        // Arrange
        var sut = CreateSut();
        sut.Subscribe<TestEvent>(_ => throw new InvalidOperationException("first"));
        sut.Subscribe<TestEvent>(_ => throw new ArgumentException("second"));

        // Act
        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        var agg = Assert.IsType<AggregateException>(ex);
        Assert.Equal(2, agg.InnerExceptions.Count);
    }

    [Fact]
    public async Task Publish_AsyncHandlerThrows_ExceptionIsPropagated()
    {
        // Arrange
        var sut = CreateSut();
        var expected = new InvalidOperationException("async-boom");
        sut.Subscribe<TestEvent>(async _ =>
        {
            await Task.Yield();
            throw expected;
        });

        // Act
        var actual = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Publish_AsyncHandlerCancelled_ThrowsTaskCanceledException()
    {
        // Arrange
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        sut.Subscribe<TestEvent>(_ => new ValueTask(Task.FromCanceled(cts.Token)));

        // Act
        var ex = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        Assert.IsType<TaskCanceledException>(ex);
    }

    [Fact]
    public async Task Publish_OneHandlerFails_RemainingHandlersAreStillInvoked()
    {
        // Arrange
        var sut = CreateSut();
        var secondCalled = false;
        sut.Subscribe<TestEvent>(_ => throw new InvalidOperationException("first fails"));
        sut.Subscribe<TestEvent>(_ => { secondCalled = true; return ValueTask.CompletedTask; });

        // Act
        await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        Assert.True(secondCalled);
    }

    // ── Unsubscribe / subscription disposal ─────────────────────────────────

    [Fact]
    public async Task Unsubscribe_PreventsSubsequentHandlerInvocation()
    {
        // Arrange
        var sut = CreateSut();
        var called = false;
        var sub = sut.Subscribe<TestEvent>(_ => { called = true; return ValueTask.CompletedTask; });

        // Act
        sut.Unsubscribe(sub);
        await sut.Publish(new TestEvent());

        // Assert
        Assert.False(called);
    }

    [Fact]
    public void Unsubscribe_AfterAggregatorDispose_DoesNotThrow()
    {
        // Arrange
        var sut = CreateSut();
        var sub = sut.Subscribe<TestEvent>(_ => ValueTask.CompletedTask);
        sut.Dispose();

        // Act
        var ex = Record.Exception(() => sut.Unsubscribe(sub));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeSubscription_PreventsSubsequentHandlerInvocation()
    {
        // Arrange
        var sut = CreateSut();
        var called = false;
        var sub = sut.Subscribe<TestEvent>(_ => { called = true; return ValueTask.CompletedTask; });

        // Act
        sub.Dispose();
        await sut.Publish(new TestEvent());

        // Assert
        Assert.False(called);
    }

    // ── Dead weak reference pruning ──────────────────────────────────────────

    // Separate non-inlined method ensures the JIT does not keep 'host' alive
    // on the calling frame's stack while GC runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SubscribeEphemeral(DefaultEventAggregator aggregator)
    {
        var host = new HandlerHost();
        aggregator.Subscribe<TestEvent>(host.Handle);
    }

    [Fact]
    public async Task Publish_HandlerTargetGarbageCollected_DoesNotThrow()
    {
        // Arrange
        var sut = CreateSut();
        SubscribeEphemeral(sut);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        // Act — first publish detects and prunes the dead subscription
        var ex1 = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Act — second publish: subscription list is now empty
        var ex2 = await Record.ExceptionAsync(() => sut.Publish(new TestEvent()).AsTask());

        // Assert
        Assert.Null(ex1);
        Assert.Null(ex2);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        // Arrange
        var sut = CreateSut();
        sut.Dispose();

        // Act
        var ex = Record.Exception(() => sut.Dispose());

        // Assert
        Assert.Null(ex);
    }

    // ── Subscribe return value ───────────────────────────────────────────────

    [Fact]
    public void Subscribe_ReturnsSubscriptionWithCorrectEventType()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var sub = sut.Subscribe<TestEvent>(_ => ValueTask.CompletedTask);

        // Assert
        Assert.Equal(typeof(TestEvent), sub.EventType);
    }
}
