using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pointframe.Data;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;
using Pointframe.Engine;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureCatalogIntegrityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Pointframe.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IndexWorker_ChangedFile_DoesNotAttachReplacementTextToOriginalHash()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "catalog.db");
        var imagePath = Path.Combine(_directory, "capture.png");
        WritePng(imagePath, Color.Red);
        var ocr = new Mock<IOcrEngineService>();
        ocr.Setup(service => service.RecognizeDetailedAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OcrRecognitionResult.Recognized("replacement"));
        using var services = CreateServices(databasePath, ocr.Object);
        var catalog = services.GetRequiredService<ICaptureCatalogService>();
        await catalog.RegisterAsync(new CaptureRegistrationRequest(imagePath, "red", "import", DateTimeOffset.UnixEpoch, "capture"));
        WritePng(imagePath, Color.Blue);

        await services.GetRequiredService<ICaptureIndexWorker>().RunOnceAsync();

        await using var context = CreateContext(databasePath);
        var artifact = await context.CaptureArtifacts.SingleAsync();
        Assert.Equal(CaptureOcrStatus.Pending, artifact.OcrStatus);
        Assert.Null(artifact.OcrText);
        Assert.Null(artifact.IndexedSha256);
        Assert.Null(artifact.LeaseOwner);
    }

    [Fact]
    public async Task IndexWorker_ExpiredLease_IsRecoveredAndCompleted()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "catalog.db");
        var imagePath = Path.Combine(_directory, "capture.png");
        WritePng(imagePath, Color.Green);
        var ocr = new Mock<IOcrEngineService>();
        ocr.Setup(service => service.RecognizeDetailedAsync(It.IsAny<Bitmap>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OcrRecognitionResult.Recognized("recovered"));
        using var services = CreateServices(databasePath, ocr.Object);
        var catalog = services.GetRequiredService<ICaptureCatalogService>();
        await catalog.RegisterAsync(new CaptureRegistrationRequest(imagePath, "green", "import", DateTimeOffset.UnixEpoch, "capture"));
        await using (var seed = CreateContext(databasePath))
        {
            var artifact = await seed.CaptureArtifacts.SingleAsync();
            artifact.OcrStatus = CaptureOcrStatus.Processing;
            artifact.LeaseOwner = "interrupted";
            artifact.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await seed.SaveChangesAsync();
        }

        await services.GetRequiredService<ICaptureIndexWorker>().RunOnceAsync();

        await using var context = CreateContext(databasePath);
        var completed = await context.CaptureArtifacts.SingleAsync();
        Assert.Equal(CaptureOcrStatus.Ready, completed.OcrStatus);
        Assert.Equal("recovered", completed.OcrText);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseExpiresUtc);
    }

    [Fact]
    public async Task Import_ReconciliationKeepsLockedObservedFileAvailable()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "catalog.db");
        var library = Path.Combine(_directory, "library");
        Directory.CreateDirectory(library);
        var imagePath = Path.Combine(library, "capture.png");
        WritePng(imagePath, Color.Green);
        using var services = CreateServices(databasePath, new Mock<IOcrEngineService>().Object, library);
        var importer = services.GetRequiredService<ICaptureImportService>();
        await importer.RequestReconciliationAsync();

        using (new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await importer.RequestReconciliationAsync();
        }

        await using var context = CreateContext(databasePath);
        Assert.Equal(CaptureArtifactAvailability.Available, (await context.CaptureArtifacts.SingleAsync()).Availability);
    }

    [Fact]
    public async Task Import_ReconciliationDoesNotMarkSubfolderArtifactMissing()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "catalog.db");
        var library = Path.Combine(_directory, "library");
        var child = Path.Combine(library, "child");
        Directory.CreateDirectory(child);
        var imagePath = Path.Combine(child, "capture.png");
        WritePng(imagePath, Color.Green);
        using var services = CreateServices(databasePath, new Mock<IOcrEngineService>().Object, library);
        var catalog = services.GetRequiredService<ICaptureCatalogService>();
        await catalog.RegisterAsync(new CaptureRegistrationRequest(imagePath, "child", "import", DateTimeOffset.UnixEpoch, "capture"));

        await services.GetRequiredService<ICaptureImportService>().RequestReconciliationAsync();

        await using var context = CreateContext(databasePath);
        Assert.Equal(CaptureArtifactAvailability.Available, (await context.CaptureArtifacts.SingleAsync()).Availability);
    }

    [Fact]
    public async Task GetCapture_RejectsAFileWhoseBytesNoLongerMatchItsArtifact()
    {
        Directory.CreateDirectory(_directory);
        var imagePath = Path.Combine(_directory, "capture.png");
        WritePng(imagePath, Color.Red);
        var redHash = HashFile(imagePath);
        var catalog = new Mock<ICaptureCatalogService>();
        catalog.Setup(service => service.GetAsync("red", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaptureCatalogArtifact(
                "red", "capture.png", redHash, "image/png", new FileInfo(imagePath).Length, 2, 2,
                DateTimeOffset.UnixEpoch, "Available", "Ready", "red text", null, imagePath));
        var tools = new PointframeMcpTools(
            new Mock<IDirectCaptureService>().Object,
            new Mock<IDirectRecordingMcpService>().Object,
            catalog.Object);
        WritePng(imagePath, Color.Blue);

        var result = await tools.GetCaptureAsync("red");

        Assert.True(result.IsError);
        Assert.Contains(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>(), block => block.Text == "artifact_changed");
    }

    [Fact]
    public async Task CatalogSearch_ReturnsCursorAndIndexState()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "catalog.db");
        using var services = CreateServices(databasePath, new Mock<IOcrEngineService>().Object);
        var catalog = services.GetRequiredService<ICaptureCatalogService>();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(_directory, $"capture-{index}.png");
            WritePng(path, Color.FromArgb(index, index, index));
            await catalog.RegisterAsync(new CaptureRegistrationRequest(
                path,
                $"artifact-{index}",
                "import",
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                "capture"));
        }

        var first = await catalog.SearchAsync(new CaptureCatalogSearchRequest(null, null, null, 2));
        var second = await catalog.SearchAsync(new CaptureCatalogSearchRequest(null, null, null, 2, first.NextCursor));

        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(3, first.IndexState.PendingCount);
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ServiceProvider CreateServices(string databasePath, IOcrEngineService ocr, string? importRoot = null)
    {
        var services = new ServiceCollection()
            .AddPointframeDataServices($"Data Source={databasePath};Pooling=False")
            .AddSingleton(TimeProvider.System)
            .AddSingleton(ocr)
            .AddSingleton<ICaptureCatalogService, CaptureCatalogService>()
            .AddSingleton<ICaptureIndexWorker, CaptureIndexWorker>();
        if (importRoot is not null)
        {
            services.AddSingleton<ICaptureLibrarySources>(new TestCaptureLibrarySources(importRoot));
            services.AddSingleton<ICaptureImportService, CaptureImportService>();
        }

        return services.BuildServiceProvider();
    }

    private static PointframeDataContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<PointframeDataContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        return new PointframeDataContext(options);
    }

    private static void WritePng(string path, Color color)
    {
        using var bitmap = new Bitmap(2, 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class TestCaptureLibrarySources(string root) : ICaptureLibrarySources
    {
        public IReadOnlyList<string> GetImportRoots() => [root];
    }
}
