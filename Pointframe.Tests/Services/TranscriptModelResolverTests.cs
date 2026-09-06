using System.IO;
using System.Reflection;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

// TranscriptModelResolver is internal, so reach it the same way the app does:
// through the AppContext override that exists as its test seam.
public sealed class TranscriptModelResolverTests : IDisposable
{
    private const string ModelPathOverrideKey = "Pointframe.WhisperModelPath";

    private readonly string _workDirectory;
    private readonly object? _originalOverride;

    public TranscriptModelResolverTests()
    {
        _originalOverride = AppContext.GetData(ModelPathOverrideKey);
        _workDirectory = Path.Combine(
            Path.GetTempPath(),
            "pointframe-model-resolver-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);
    }

    public void Dispose()
    {
        AppContext.SetData(ModelPathOverrideKey, _originalOverride);
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static string? Resolve()
    {
        var type = typeof(TranscriptionService).Assembly
            .GetType("Pointframe.Services.TranscriptModelResolver", throwOnError: true)!;
        var method = type.GetMethod(
            "ResolveModelPath",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        return (string?)method.Invoke(null, null);
    }

    [Fact]
    public void ExistingOverride_Wins()
    {
        var modelPath = Path.Combine(_workDirectory, "custom-model.bin");
        File.WriteAllText(modelPath, "model");
        AppContext.SetData(ModelPathOverrideKey, modelPath);

        Assert.Equal(modelPath, Resolve());
    }

    [Fact]
    public void OverridePointingAtMissingFile_IsIgnored()
    {
        // A stale override must not report a model that is not there — otherwise the
        // caller skips its graceful "model not found" path and fails inside Whisper.
        AppContext.SetData(ModelPathOverrideKey, Path.Combine(_workDirectory, "not-created.bin"));

        Assert.NotEqual(Path.Combine(_workDirectory, "not-created.bin"), Resolve());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverride_FallsThroughToDiskProbing(string? overrideValue)
    {
        AppContext.SetData(ModelPathOverrideKey, overrideValue);

        // Whatever it finds, it must never hand back the blank override itself.
        var resolved = Resolve();

        Assert.True(resolved is null || File.Exists(resolved));
    }

    [Fact]
    public void ResolvedPath_AlwaysPointsAtAFileThatExists()
    {
        AppContext.SetData(ModelPathOverrideKey, null);

        var resolved = Resolve();

        Assert.True(
            resolved is null || File.Exists(resolved),
            $"Resolver returned '{resolved}', which does not exist.");
    }
}
