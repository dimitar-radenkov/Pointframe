using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Pointframe.Services;

public sealed class RecordingEventTrack : IRecordingEventTrack
{
    public const int CurrentSchemaVersion = 1;

    private const int BufferCapacity = 64;

    private readonly object _writeLock = new();
    private readonly Channel<RecordingEvent> _events;
    private readonly Stopwatch _sessionStopwatch;
    private readonly string _sidecarPath;
    private readonly Task _writerTask;
    private long _sequence;
    private long _eventCount;
    private bool _isCompleted;
    private RecordingEventTrackSummary? _summary;

    public RecordingEventTrack(string recordingPath, Stopwatch sessionStopwatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        ArgumentNullException.ThrowIfNull(sessionStopwatch);

        _sessionStopwatch = sessionStopwatch;
        _sidecarPath = Path.GetFullPath($"{recordingPath}.events.jsonl");
        _events = Channel.CreateBounded<RecordingEvent>(new BoundedChannelOptions(BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _writerTask = Task.Run(WriteEventsAsync);
    }

    public void Write(string eventType, RecordingEventPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        lock (_writeLock)
        {
            if (_isCompleted)
            {
                throw new InvalidOperationException("The recording event track has already been completed.");
            }

            var recordingEvent = new RecordingEvent(
                CurrentSchemaVersion,
                _sequence + 1,
                Math.Max(0, (long)_sessionStopwatch.Elapsed.TotalMilliseconds),
                eventType,
                payload);

            if (!_events.Writer.TryWrite(recordingEvent))
            {
                throw new InvalidOperationException("The recording event track is unable to accept another event.");
            }

            _sequence = recordingEvent.Sequence;
            _eventCount++;
        }
    }

    public RecordingEventTrackSummary Complete()
    {
        lock (_writeLock)
        {
            if (_summary is not null)
            {
                return _summary;
            }

            _isCompleted = true;
            _events.Writer.TryComplete();
        }

        _writerTask.GetAwaiter().GetResult();
        _summary = new RecordingEventTrackSummary(_sidecarPath, _eventCount, CurrentSchemaVersion);
        return _summary;
    }

    private async Task WriteEventsAsync()
    {
        var directoryPath = Path.GetDirectoryName(_sidecarPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        await using var stream = new FileStream(
            _sidecarPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await foreach (var recordingEvent in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(recordingEvent)).ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }
}
