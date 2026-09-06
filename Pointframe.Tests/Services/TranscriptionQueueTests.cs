using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TranscriptionQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static TranscriptionResult Success(string path) =>
        new(true, path + ".srt", path + ".txt", null, null, 3);

    private static TranscriptionQueue CreateQueue(ITranscriptionService service) =>
        new(service, NullLogger<TranscriptionQueue>.Instance);

    [Fact]
    public async Task SecondRecording_DoesNotCancelTheFirstTranscript()
    {
        // The first job blocks until released, so the second is definitely enqueued
        // while the first is still running — the exact race that used to cancel it.
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var transcribed = new List<string>();

        var service = new Mock<ITranscriptionService>();
        service
            .Setup(s => s.TranscribeVideoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, CancellationToken _) =>
            {
                if (path == "a.mp4")
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                lock (transcribed)
                {
                    transcribed.Add(path);
                }

                return Success(path);
            });

        using var queue = CreateQueue(service.Object);
        var completed = new List<string>();
        queue.Completed += c =>
        {
            lock (completed)
            {
                completed.Add(c.VideoPath);
            }
        };

        queue.Enqueue("a.mp4");
        await firstStarted.Task.WaitAsync(Timeout);
        queue.Enqueue("b.mp4");
        releaseFirst.SetResult();

        Assert.True(await queue.WaitForIdle(Timeout));

        Assert.Equal(new[] { "a.mp4", "b.mp4" }, transcribed);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, completed);
    }

    [Fact]
    public async Task Jobs_RunOneAtATime()
    {
        var concurrent = 0;
        var maxConcurrent = 0;

        var service = new Mock<ITranscriptionService>();
        service
            .Setup(s => s.TranscribeVideoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, CancellationToken _) =>
            {
                var running = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, running);
                await Task.Delay(20);
                Interlocked.Decrement(ref concurrent);
                return Success(path);
            });

        using var queue = CreateQueue(service.Object);
        for (var i = 0; i < 5; i++)
        {
            queue.Enqueue($"clip{i}.mp4");
        }

        Assert.True(await queue.WaitForIdle(Timeout));
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task FailingJob_DoesNotStopLaterJobs()
    {
        var service = new Mock<ITranscriptionService>();
        service
            .Setup(s => s.TranscribeVideoAsync("bad.mp4", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        service
            .Setup(s => s.TranscribeVideoAsync("good.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success("good.mp4"));

        using var queue = CreateQueue(service.Object);
        var completed = new List<TranscriptionCompletion>();
        queue.Completed += c =>
        {
            lock (completed)
            {
                completed.Add(c);
            }
        };

        queue.Enqueue("bad.mp4");
        queue.Enqueue("good.mp4");

        Assert.True(await queue.WaitForIdle(Timeout));

        Assert.Equal(2, completed.Count);
        Assert.False(completed[0].Result.Success);
        Assert.Equal("boom", completed[0].Result.ErrorMessage);
        Assert.True(completed[1].Result.Success);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotKillTheQueue()
    {
        var service = new Mock<ITranscriptionService>();
        service
            .Setup(s => s.TranscribeVideoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult(Success(path)));

        using var queue = CreateQueue(service.Object);
        var seen = new List<string>();
        queue.Completed += c =>
        {
            lock (seen)
            {
                seen.Add(c.VideoPath);
            }

            throw new InvalidOperationException("subscriber blew up");
        };

        queue.Enqueue("first.mp4");
        queue.Enqueue("second.mp4");

        Assert.True(await queue.WaitForIdle(Timeout));
        Assert.Equal(new[] { "first.mp4", "second.mp4" }, seen);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZeroAndReportsActivity()
    {
        var service = new Mock<ITranscriptionService>();
        service
            .Setup(s => s.TranscribeVideoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => Task.FromResult(Success(path)));

        using var queue = CreateQueue(service.Object);
        var activityRaised = 0;
        queue.ActivityChanged += () => Interlocked.Increment(ref activityRaised);

        queue.Enqueue("one.mp4");
        queue.Enqueue("two.mp4");

        Assert.True(await queue.WaitForIdle(Timeout));

        Assert.Equal(0, queue.PendingCount);
        Assert.False(queue.IsBusy);
        Assert.True(activityRaised > 0);
    }

    [Fact]
    public async Task WaitForIdle_IsImmediateWhenNothingWasQueued()
    {
        var queue = CreateQueue(Mock.Of<ITranscriptionService>());
        using (queue)
        {
            Assert.True(await queue.WaitForIdle(TimeSpan.Zero));
        }
    }

    [Fact]
    public void EnqueueAfterCancelAll_IsIgnored()
    {
        var service = new Mock<ITranscriptionService>();
        using var queue = CreateQueue(service.Object);

        queue.CancelAll();
        queue.Enqueue("late.mp4");

        service.Verify(
            s => s.TranscribeVideoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
