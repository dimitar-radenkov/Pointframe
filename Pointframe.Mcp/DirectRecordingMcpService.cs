using System.Text.Json;
using Pointframe.Engine;

namespace Pointframe.Mcp;

public interface IDirectRecordingMcpService
{
    string StartRecording(string monitorName, IReadOnlyList<PixelBounds> redactionRegionsCaptureLocalPixels, int framesPerSecond);

    Task<string> StopRecordingAsync(CancellationToken cancellationToken = default);
}

public sealed class DirectRecordingMcpService : IDirectRecordingMcpService
{
    private const int SchemaVersion = 1;
    private readonly IDirectRecordingService _directRecordingService;

    public DirectRecordingMcpService(IDirectRecordingService directRecordingService)
    {
        _directRecordingService = directRecordingService;
    }

    public string StartRecording(string monitorName, IReadOnlyList<PixelBounds> redactionRegionsCaptureLocalPixels, int framesPerSecond)
    {
        var result = _directRecordingService.Start(new DirectRecordingRequest(
            monitorName,
            redactionRegionsCaptureLocalPixels.Select(region => new Pointframe.Engine.PixelBounds(region.X, region.Y, region.Width, region.Height)).ToArray(),
            framesPerSecond));
        return JsonSerializer.Serialize(ToResponse(result));
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        var result = await _directRecordingService.StopAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(ToResponse(result));
    }

    private static DirectRecordingResponse ToResponse(DirectRecordingResult result)
    {
        return new DirectRecordingResponse(
            SchemaVersion,
            result.Success,
            result.ErrorCode is null ? null : new DirectCaptureError(result.ErrorCode, result.ErrorMessage ?? "Unknown error."),
            result.Session,
            result.Artifact);
    }
}

public sealed record DirectRecordingResponse(
    int SchemaVersion,
    bool Success,
    DirectCaptureError? Error = null,
    DirectRecordingSession? Session = null,
    DirectRecordingArtifact? Artifact = null);