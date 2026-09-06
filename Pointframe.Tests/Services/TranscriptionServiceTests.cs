using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TranscriptionServiceTests : IDisposable
{
    private const string ModelPathOverrideKey = "Pointframe.WhisperModelPath";

    private readonly string _workDirectory;
    private readonly string _videoPath;
    private readonly string _modelPath;

    public TranscriptionServiceTests()
    {
        _workDirectory = Path.Combine(
            Path.GetTempPath(),
            "pointframe-transcription-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);

        _videoPath = Path.Combine(_workDirectory, "SnipRec-test.mp4");
        File.WriteAllText(_videoPath, "not a real video");

        _modelPath = Path.Combine(_workDirectory, "model.bin");
        File.WriteAllText(_modelPath, "not a real model");
        AppContext.SetData(ModelPathOverrideKey, _modelPath);
    }

    public void Dispose()
    {
        AppContext.SetData(ModelPathOverrideKey, null);
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static TranscriptSegment Segment(int start, int end, string text) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    private static async IAsyncEnumerable<TranscriptSegment> Stream(params TranscriptSegment[] segments)
    {
        foreach (var segment in segments)
        {
            yield return segment;
        }

        await Task.CompletedTask;
    }

    private string CreateTempWav()
    {
        var path = Path.Combine(_workDirectory, Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllText(path, "wav");
        return path;
    }

    private TranscriptionService CreateService(
        ISpeechRecognizer recognizer,
        IAudioExtractor? extractor = null,
        string? wavPath = null,
        string? modelPath = "<default>")
    {
        if (extractor is null)
        {
            var stub = new Mock<IAudioExtractor>();
            stub
                .Setup(e => e.ExtractWavAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => wavPath ?? CreateTempWav());
            extractor = stub.Object;
        }

        var resolved = modelPath == "<default>" ? _modelPath : modelPath;
        var modelService = new Mock<ITranscriptModelService>();
        modelService.Setup(m => m.ResolveModelPath()).Returns(resolved);
        modelService.SetupGet(m => m.IsModelInstalled).Returns(resolved is not null);

        return new TranscriptionService(
            extractor,
            recognizer,
            modelService.Object,
            NullLogger<TranscriptionService>.Instance);
    }

    [Fact]
    public async Task MissingModel_SkipsGracefullyWithoutTouchingFfmpeg()
    {
        var extractor = new Mock<IAudioExtractor>();

        var service = CreateService(Mock.Of<ISpeechRecognizer>(), extractor.Object, modelPath: null);
        var result = await service.TranscribeVideoAsync(_videoPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(TranscriptionSkipReasons.ModelNotFound, result.SkipReason);
        Assert.Null(result.ErrorMessage);
        extractor.Verify(
            e => e.ExtractWavAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NoSegments_ReportsNoSpeechAndWritesNothing()
    {
        var recognizer = new Mock<ISpeechRecognizer>();
        recognizer
            .Setup(r => r.TranscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Stream());

        var result = await CreateService(recognizer.Object).TranscribeVideoAsync(_videoPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(TranscriptionSkipReasons.NoSpeechDetected, result.SkipReason);
        Assert.False(File.Exists(Path.ChangeExtension(_videoPath, ".srt")));
        Assert.False(File.Exists(Path.ChangeExtension(_videoPath, ".txt")));
    }

    [Fact]
    public async Task OnlyBlankSegments_ReportsNoSpeechInsteadOfEmptySuccess()
    {
        var recognizer = new Mock<ISpeechRecognizer>();
        recognizer
            .Setup(r => r.TranscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(Segment(0, 2, "   "), Segment(2, 4, string.Empty)));

        var result = await CreateService(recognizer.Object).TranscribeVideoAsync(_videoPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(TranscriptionSkipReasons.NoSpeechDetected, result.SkipReason);
        Assert.False(File.Exists(Path.ChangeExtension(_videoPath, ".txt")));
    }

    [Fact]
    public async Task Success_WritesBothSidecarsWithoutAByteOrderMark()
    {
        var recognizer = new Mock<ISpeechRecognizer>();
        recognizer
            .Setup(r => r.TranscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(Segment(0, 2, "Hello there")));

        var result = await CreateService(recognizer.Object).TranscribeVideoAsync(_videoPath, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.SegmentCount);

        var srtPath = Path.ChangeExtension(_videoPath, ".srt");
        var txtPath = Path.ChangeExtension(_videoPath, ".txt");
        Assert.Equal(srtPath, result.SrtPath);
        Assert.Equal(txtPath, result.TxtPath);

        // A BOM before the first cue index makes strict SRT parsers drop subtitle 1.
        var preamble = Encoding.UTF8.GetPreamble();
        var srtBytes = await File.ReadAllBytesAsync(srtPath);
        Assert.False(
            srtBytes.Take(preamble.Length).SequenceEqual(preamble),
            "SRT must not start with a byte-order mark.");
        Assert.Equal((byte)'1', srtBytes[0]);

        var txtBytes = await File.ReadAllBytesAsync(txtPath);
        Assert.False(
            txtBytes.Take(preamble.Length).SequenceEqual(preamble),
            "Transcript text must not start with a byte-order mark.");
    }

    [Fact]
    public async Task RecognizerFailure_ReportsErrorRatherThanSkip()
    {
        var recognizer = new Mock<ISpeechRecognizer>();
        recognizer
            .Setup(r => r.TranscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("whisper exploded"));

        var result = await CreateService(recognizer.Object).TranscribeVideoAsync(_videoPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.SkipReason);
        Assert.Equal("whisper exploded", result.ErrorMessage);
    }

    [Fact]
    public async Task TempWavIsDeleted_OnSuccessAndOnFailure()
    {
        var successWav = CreateTempWav();
        var okRecognizer = new Mock<ISpeechRecognizer>();
        okRecognizer
            .Setup(r => r.TranscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Stream(Segment(0, 1, "Hi")));
        await CreateService(okRecognizer.Object, wavPath: successWav)
            .TranscribeVideoAsync(_videoPath, CancellationToken.None);
        Assert.False(File.Exists(successWav));

        var failureWav = CreateTempWav();
        var badRecognizer = new Mock<ISpeechRecognizer>();
        badRecognizer
            .Setup(r => r.TranscribeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("boom"));
        await CreateService(badRecognizer.Object, wavPath: failureWav)
            .TranscribeVideoAsync(_videoPath, CancellationToken.None);
        Assert.False(File.Exists(failureWav));
    }

    [Fact]
    public async Task Cancellation_PropagatesToCaller()
    {
        var extractor = new Mock<IAudioExtractor>();
        extractor
            .Setup(e => e.ExtractWavAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(Mock.Of<ISpeechRecognizer>(), extractor.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.TranscribeVideoAsync(_videoPath, new CancellationToken(canceled: true)));
    }
}
