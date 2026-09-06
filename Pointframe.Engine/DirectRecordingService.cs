using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pointframe.Engine;

public sealed record DirectRecordingRequest(
    string MonitorName,
    IReadOnlyList<PixelBounds> RedactionRegionsCaptureLocalPixels,
    int FramesPerSecond = 20,
    string? OutputDirectory = null);

public sealed record DirectRecordingArtifact(
    int SchemaVersion,
    string ArtifactId,
    string Kind,
    string Path,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedUtc,
    TimeSpan ElapsedDuration,
    bool HadMicrophoneAudio,
    string MonitorName,
    double DpiScaleX,
    double DpiScaleY,
    PixelBounds CaptureBoundsPixels,
    PixelBounds HostBoundsPixels,
    PixelBounds WorkAreaBoundsPixels,
    string EventSidecarPath,
    long EventCount,
    int EventTrackSchemaVersion);

public sealed record DirectRecordingSession(
    int SchemaVersion,
    string OperationId,
    string ArtifactPath,
    string MonitorName,
    int FramesPerSecond,
    PixelBounds CaptureBoundsPixels,
    IReadOnlyList<PixelBounds> RedactionRegionsCaptureLocalPixels,
    DateTimeOffset StartedUtc);

public sealed record DirectRecordingResult(
    bool Success,
    DirectRecordingSession? Session = null,
    DirectRecordingArtifact? Artifact = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IDirectRecordingService : IDisposable
{
    DirectRecordingResult Start(DirectRecordingRequest request);

    Task<DirectRecordingResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface IDirectVideoWriter : IDisposable
{
    void WriteFrame(byte[] frameData);
}

public interface IDirectVideoWriterFactory
{
    IDirectVideoWriter Create(int width, int height, int framesPerSecond, string outputPath);
}

public sealed class DirectRecordingService : IDirectRecordingService
{
    private const int SchemaVersion = 1;
    private const int EventTrackSchemaVersion = 1;
    private const int MinimumFramesPerSecond = 1;
    private const int MaximumFramesPerSecond = 60;
    private static readonly JsonSerializerOptions MetadataSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object _sessionLock = new();
    private readonly IDisplayCaptureEngine _displayCaptureEngine;
    private readonly IDirectVideoWriterFactory _videoWriterFactory;
    private readonly TimeProvider _timeProvider;
    private ActiveSession? _activeSession;

    public DirectRecordingService(
        IDisplayCaptureEngine displayCaptureEngine,
        IDirectVideoWriterFactory videoWriterFactory,
        TimeProvider? timeProvider = null)
    {
        _displayCaptureEngine = displayCaptureEngine;
        _videoWriterFactory = videoWriterFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DirectRecordingResult Start(DirectRecordingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sessionLock)
        {
            if (_activeSession is not null)
            {
                return Failure("recording_already_active", "A direct recording session is already active. Stop it before starting another recording.");
            }

            if (string.IsNullOrWhiteSpace(request.MonitorName))
            {
                return Failure("invalid_monitor_name", "MonitorName is required.");
            }

            if (request.RedactionRegionsCaptureLocalPixels is null)
            {
                return Failure("redaction_regions_required", "RedactionRegionsCaptureLocalPixels must be declared. Use an empty array when no redaction is needed.");
            }

            if (request.FramesPerSecond is < MinimumFramesPerSecond or > MaximumFramesPerSecond)
            {
                return Failure("invalid_frames_per_second", $"FramesPerSecond must be between {MinimumFramesPerSecond} and {MaximumFramesPerSecond}.");
            }

            var display = _displayCaptureEngine.GetDisplays().SingleOrDefault(candidate =>
                string.Equals(candidate.MonitorName, request.MonitorName, StringComparison.OrdinalIgnoreCase));
            if (display is null)
            {
                return Failure("monitor_not_found", $"The monitor '{request.MonitorName}' was not found.");
            }

            var bounds = NormalizeEvenBounds(display.BoundsPixels);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return Failure("invalid_capture_bounds", "The selected monitor is too small to create an MP4 recording.");
            }

            if (request.RedactionRegionsCaptureLocalPixels.Any(region => region.Width <= 0 || region.Height <= 0))
            {
                return Failure("invalid_redaction_region", "Each redaction region must have positive width and height in capture-local physical pixels.");
            }

            var outputDirectory = request.OutputDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pointframe",
                "Recordings");
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Failure("invalid_output_directory", "OutputDirectory must not be empty when supplied.");
            }

            Directory.CreateDirectory(outputDirectory);
            var startedUtc = _timeProvider.GetUtcNow();
            var artifactId = $"rec_{Guid.NewGuid():N}";
            var outputPath = Path.Combine(outputDirectory, $"{startedUtc:yyyyMMdd-HHmmss}-{artifactId}.mp4");
            var stopwatch = Stopwatch.StartNew();
            var eventTrack = new DirectRecordingEventTrack(outputPath, stopwatch);

            try
            {
                var writer = _videoWriterFactory.Create(bounds.Width, bounds.Height, request.FramesPerSecond, outputPath);
                var redactionRegions = request.RedactionRegionsCaptureLocalPixels.ToArray();
                var directSession = new DirectRecordingSession(
                        SchemaVersion,
                        artifactId,
                        outputPath,
                        display.MonitorName,
                        request.FramesPerSecond,
                        bounds,
                        redactionRegions,
                        startedUtc);
                eventTrack.Write("recording.started", new DirectRecordingEventPayload(
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    request.FramesPerSecond));
                var redactionRevision = 0L;
                foreach (var region in directSession.RedactionRegionsCaptureLocalPixels)
                {
                    eventTrack.Write("redaction.added", new DirectRecordingEventPayload(
                        RedactionX: region.X,
                        RedactionY: region.Y,
                        RedactionWidth: region.Width,
                        RedactionHeight: region.Height,
                        RedactionRevision: ++redactionRevision,
                        RedactionMode: "pixelate",
                        RedactionOperation: "added"));
                }

                var pipeline = new RawFrameRecordingPipeline(
                    new DirectRawFrameWriter(writer),
                    new RawFrameRecordingOptions(
                        directSession.CaptureBoundsPixels,
                        directSession.FramesPerSecond,
                        () => redactionRegions));
                var session = new ActiveSession(directSession, display, writer, pipeline, eventTrack, stopwatch);
                _activeSession = session;
                return new DirectRecordingResult(true, Session: session.Session);
            }
            catch (Exception exception)
            {
                eventTrack.Dispose();
                return Failure("recording_start_failed", exception.Message);
            }
        }
    }

    public async Task<DirectRecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ActiveSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
            if (session is null)
            {
                return Failure("recording_not_active", "There is no active direct recording session to stop.");
            }

            _activeSession = null;
        }

        try
        {
            session.Pipeline.Stop(session.Stopwatch.Elapsed);
            session.EventTrack.Write("recording.stopped", new DirectRecordingEventPayload());
            session.Pipeline.Dispose();
            session.Writer.Dispose();
            var eventSummary = session.EventTrack.Complete();
            var artifact = await CreateArtifactAsync(session, eventSummary, cancellationToken).ConfigureAwait(false);
            return new DirectRecordingResult(true, Artifact: artifact);
        }
        catch (Exception exception)
        {
            return Failure("recording_stop_failed", exception.Message);
        }
        finally
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task<DirectRecordingArtifact> CreateArtifactAsync(
        ActiveSession session,
        DirectRecordingEventSummary eventSummary,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(session.Session.ArtifactPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("ffmpeg did not produce a recording artifact.", file.FullName);
        }

        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var artifact = new DirectRecordingArtifact(
            SchemaVersion,
            session.Session.OperationId,
            "video/mp4",
            file.FullName,
            Convert.ToHexStringLower(hash),
            file.Length,
            session.Session.StartedUtc,
            session.Stopwatch.Elapsed,
            false,
            session.Display.MonitorName,
            session.Display.DpiScaleX,
            session.Display.DpiScaleY,
            session.Session.CaptureBoundsPixels,
            session.Session.CaptureBoundsPixels,
            session.Display.WorkAreaBoundsPixels.Width > 0 && session.Display.WorkAreaBoundsPixels.Height > 0
                ? session.Display.WorkAreaBoundsPixels
                : session.Display.BoundsPixels,
            eventSummary.SidecarPath,
            eventSummary.EventCount,
            eventSummary.SchemaVersion);
        await WriteMetadataSidecarAsync(artifact, cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    private static DirectRecordingResult Failure(string code, string message)
    {
        return new DirectRecordingResult(false, ErrorCode: code, ErrorMessage: message);
    }

    private static PixelBounds NormalizeEvenBounds(PixelBounds bounds)
    {
        return new PixelBounds(bounds.X, bounds.Y, bounds.Width - (bounds.Width % 2), bounds.Height - (bounds.Height % 2));
    }

    private static async Task WriteMetadataSidecarAsync(DirectRecordingArtifact artifact, CancellationToken cancellationToken)
    {
        var metadataPath = $"{artifact.Path}.metadata.json";
        var temporaryPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(artifact, MetadataSerializerOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class ActiveSession : IDisposable
    {
        public ActiveSession(DirectRecordingSession session, DisplayDescriptor display, IDirectVideoWriter writer, RawFrameRecordingPipeline pipeline, DirectRecordingEventTrack eventTrack, Stopwatch stopwatch)
        {
            Session = session;
            Display = display;
            Writer = writer;
            Pipeline = pipeline;
            EventTrack = eventTrack;
            Stopwatch = stopwatch;
        }

        public DirectRecordingSession Session { get; }
        public DisplayDescriptor Display { get; }
        public IDirectVideoWriter Writer { get; }
        public RawFrameRecordingPipeline Pipeline { get; }
        public DirectRecordingEventTrack EventTrack { get; }
        public Stopwatch Stopwatch { get; }
        public void Dispose()
        {
            Pipeline.Dispose();
            Writer.Dispose();
            EventTrack.Dispose();
        }
    }

    private sealed class DirectRawFrameWriter(IDirectVideoWriter writer) : IRawFrameWriter
    {
        public void WriteFrame(byte[] frameData)
        {
            writer.WriteFrame(frameData);
        }
    }
}

internal sealed record DirectRecordingEventPayload(
    int? CaptureX = null,
    int? CaptureY = null,
    int? CaptureWidth = null,
    int? CaptureHeight = null,
    int? FramesPerSecond = null,
    int? RedactionX = null,
    int? RedactionY = null,
    int? RedactionWidth = null,
    int? RedactionHeight = null,
    long? RedactionRevision = null,
    string? RedactionMode = null,
    string? RedactionOperation = null);

internal sealed record DirectRecordingEvent(int SchemaVersion, long Sequence, long RelativeTimestampMilliseconds, string EventType, DirectRecordingEventPayload Payload);

internal sealed record DirectRecordingEventSummary(string SidecarPath, long EventCount, int SchemaVersion);

internal sealed class DirectRecordingEventTrack : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly object _writeLock = new();
    private readonly Stopwatch _stopwatch;
    private readonly StreamWriter _writer;
    private bool _completed;
    private long _sequence;

    public DirectRecordingEventTrack(string recordingPath, Stopwatch stopwatch)
    {
        var sidecarPath = Path.GetFullPath($"{recordingPath}.events.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
        _stopwatch = stopwatch;
        _writer = new StreamWriter(new FileStream(sidecarPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.None), new UTF8Encoding(false));
        SidecarPath = sidecarPath;
    }

    public string SidecarPath { get; }
    public long EventCount { get; private set; }

    public void Write(string eventType, DirectRecordingEventPayload payload)
    {
        lock (_writeLock)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The recording event track has already been completed.");
            }

            var recordingEvent = new DirectRecordingEvent(SchemaVersion, ++_sequence, Math.Max(0, (long)_stopwatch.Elapsed.TotalMilliseconds), eventType, payload);
            _writer.WriteLine(JsonSerializer.Serialize(recordingEvent));
            _writer.Flush();
            EventCount++;
        }
    }

    public DirectRecordingEventSummary Complete()
    {
        lock (_writeLock)
        {
            if (!_completed)
            {
                _completed = true;
                _writer.Dispose();
            }

            return new DirectRecordingEventSummary(SidecarPath, EventCount, SchemaVersion);
        }
    }

    public void Dispose()
    {
        Complete();
    }
}