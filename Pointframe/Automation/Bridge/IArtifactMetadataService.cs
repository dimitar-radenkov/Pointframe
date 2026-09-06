namespace Pointframe.Automation.Bridge;

internal interface IArtifactMetadataService
{
    Task<ImageArtifactMetadata> WriteImageMetadataAsync(
        ImageArtifactMetadataRequest request,
        CancellationToken cancellationToken = default);

    Task<RecordingArtifactMetadata> WriteRecordingMetadataAsync(
        RecordingArtifactMetadataRequest request,
        CancellationToken cancellationToken = default);
}
