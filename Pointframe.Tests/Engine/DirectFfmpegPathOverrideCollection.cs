using Xunit;

namespace Pointframe.Tests.Engine;

/// <summary>
/// Groups tests that read or mutate the process-wide POINTFRAME_FFMPEG_PATH environment variable consumed by
/// Pointframe.Engine's FfmpegDirectVideoWriter, so they never run concurrently with each other.
/// </summary>
[CollectionDefinition("DirectFfmpegPathOverride", DisableParallelization = true)]
public sealed class DirectFfmpegPathOverrideCollection
{
}
