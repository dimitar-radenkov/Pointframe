using System.Diagnostics;
using System.Threading.Channels;

namespace Pointframe.Services;

// Transcription is CPU-bound and roughly real-time, so a second recording can easily
// finish while the first is still being transcribed. Queueing serially keeps every
// transcript rather than letting a newer recording cancel an older one's job, and it
// avoids running two Whisper sessions against the same CPU at once.
public sealed class TranscriptionQueue : ITranscriptionQueue, IDisposable
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly ITranscriptionService _transcriptionService;
    private readonly ILogger<TranscriptionQueue> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;
    private readonly object _idleLock = new();

    private int _pendingCount;
    private volatile bool _isBusy;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TranscriptionQueue(
        ITranscriptionService transcriptionService,
        ILogger<TranscriptionQueue> logger)
    {
        _transcriptionService = transcriptionService;
        _logger = logger;
        _idle.TrySetResult();
        _consumer = Task.Run(ConsumeAsync);
    }

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public bool IsBusy => _isBusy;

    public event Action? ActivityChanged;

    public event Action<TranscriptionCompletion>? Completed;

    public void Enqueue(string videoPath)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        lock (_idleLock)
        {
            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        Interlocked.Increment(ref _pendingCount);

        if (!_queue.Writer.TryWrite(videoPath))
        {
            Interlocked.Decrement(ref _pendingCount);
            SignalIfIdle();
            return;
        }

        _logger.LogInformation("Queued transcription for {Video} ({Pending} pending)", videoPath, PendingCount);
        ActivityChanged?.Invoke();
    }

    public async Task<bool> WaitForIdle(TimeSpan timeout)
    {
        Task idle;
        lock (_idleLock)
        {
            idle = _idle.Task;
        }

        if (idle.IsCompleted)
        {
            return true;
        }

        var completed = await Task.WhenAny(idle, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == idle;
    }

    public void CancelAll()
    {
        _queue.Writer.TryComplete();
        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var videoPath in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                _isBusy = true;
                ActivityChanged?.Invoke();

                var stopwatch = Stopwatch.StartNew();
                TranscriptionResult? result = null;
                try
                {
                    result = await _transcriptionService
                        .TranscribeVideoAsync(videoPath, _shutdown.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Transcription cancelled for {Video}", videoPath);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transcription failed for {Video}", videoPath);
                    result = new TranscriptionResult(false, null, null, null, ex.Message);
                }
                finally
                {
                    stopwatch.Stop();
                    _isBusy = false;
                    Interlocked.Decrement(ref _pendingCount);
                }

                if (result is not null)
                {
                    // A throwing subscriber must not kill the consumer loop, or every
                    // later recording would silently stop being transcribed.
                    try
                    {
                        Completed?.Invoke(new TranscriptionCompletion(result, videoPath, stopwatch.Elapsed));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Transcription completion handler failed for {Video}", videoPath);
                    }
                }

                SignalIfIdle();
                ActivityChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        finally
        {
            _isBusy = false;
            Volatile.Write(ref _pendingCount, 0);
            SignalIfIdle();
            ActivityChanged?.Invoke();
        }
    }

    private void SignalIfIdle()
    {
        if (PendingCount > 0)
        {
            return;
        }

        lock (_idleLock)
        {
            _idle.TrySetResult();
        }
    }

    public void Dispose()
    {
        CancelAll();
        try
        {
            _consumer.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Expected when the consumer observes cancellation.
        }

        _shutdown.Dispose();
    }
}
