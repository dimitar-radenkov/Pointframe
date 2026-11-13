using Xunit;

namespace Pointframe.Tests.Services;

// Groups tests that set the process-wide AppContext key Pointframe.WhisperModelPath read by TranscriptModelResolver,
// so one class never sees the other's override.
[CollectionDefinition("WhisperModelPathOverride", DisableParallelization = true)]
public sealed class WhisperModelPathOverrideCollection
{
}
