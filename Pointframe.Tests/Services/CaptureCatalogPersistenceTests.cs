using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;
using Pointframe.Data.Repository;
using Pointframe.Data;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CaptureCatalogPersistenceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Pointframe.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Migrate_ExistingCacheDatabase_PreservesCacheAndAddsCatalogTables()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "catalog.db");

        await using (var oldContext = CreateContext(databasePath))
        {
            var migrator = oldContext.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260712054819_Initialize");
            oldContext.CaptureTextCacheEntries.Add(new CaptureTextCacheEntry
            {
                FilePath = "existing.png",
                CapturedAt = DateTime.UnixEpoch,
                LastAccessedAt = DateTime.UnixEpoch,
            });
            await oldContext.SaveChangesAsync();
        }

        await using var context = CreateContext(databasePath);
        await context.Database.MigrateAsync();

        Assert.NotNull(await context.CaptureTextCacheEntries.SingleOrDefaultAsync(entry => entry.FilePath == "existing.png"));
        Assert.Empty(await context.CaptureArtifacts.ToListAsync());
        Assert.Empty(await context.CaptureLocations.ToListAsync());
    }

    [Fact]
    public async Task CatalogRepository_PendingWork_ReturnsOnlyAvailableDueArtifacts()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "catalog.db");
        var now = new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

        await using (var seedContext = CreateContext(databasePath))
        {
            await seedContext.Database.MigrateAsync();
            seedContext.CaptureArtifacts.AddRange(
                CreateArtifact("due", CaptureArtifactAvailability.Available, CaptureOcrStatus.Pending, now.AddMinutes(-1)),
                CreateArtifact("later", CaptureArtifactAvailability.Available, CaptureOcrStatus.Pending, now.AddMinutes(1)),
                CreateArtifact("ready", CaptureArtifactAvailability.Available, CaptureOcrStatus.Ready, null),
                CreateArtifact("missing", CaptureArtifactAvailability.Missing, CaptureOcrStatus.Pending, null));
            await seedContext.SaveChangesAsync();
        }

        await using var queryContext = CreateContext(databasePath);
        var sut = new CaptureCatalogRepository(queryContext);

        var result = await sut.GetPendingOcrArtifacts(now, 100);

        var artifact = Assert.Single(result);
        Assert.Equal("due", artifact.ArtifactId);
    }

    [Fact]
    public async Task CaptureCatalogService_Overwrite_CreatesNewArtifactAndSupersedesOldGeneration()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "catalog.db");
        var imagePath = Path.Combine(_tempDirectory, "capture.png");
        WritePng(imagePath, System.Drawing.Color.Red);
        using var services = new ServiceCollection()
            .AddPointframeDataServices($"Data Source={databasePath};Pooling=False")
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ICaptureCatalogService, CaptureCatalogService>()
            .BuildServiceProvider();
        var sut = services.GetRequiredService<ICaptureCatalogService>();

        var first = await sut.RegisterAsync(new CaptureRegistrationRequest(
            imagePath,
            "first",
            "wpf_save",
            DateTimeOffset.UnixEpoch,
            "capture"));
        WritePng(imagePath, System.Drawing.Color.Blue);
        var second = await sut.RegisterAsync(new CaptureRegistrationRequest(
            imagePath,
            "second",
            "wpf_save",
            DateTimeOffset.UnixEpoch,
            "capture"));

        Assert.Equal("first", first.ArtifactId);
        Assert.Equal("second", second.ArtifactId);
        await using var context = CreateContext(databasePath);
        var firstArtifact = await context.CaptureArtifacts.SingleAsync(artifact => artifact.ArtifactId == "first");
        var location = await context.CaptureLocations.SingleAsync();
        Assert.Equal(CaptureArtifactAvailability.Superseded, firstArtifact.Availability);
        Assert.Equal("second", location.CurrentArtifactId);
    }

    [Fact]
    public async Task CaptureImportService_ReconcilesLegacyFileThenMarksDeletedFileMissing()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "catalog.db");
        var libraryPath = Path.Combine(_tempDirectory, "library");
        Directory.CreateDirectory(libraryPath);
        var imagePath = Path.Combine(libraryPath, "legacy.png");
        WritePng(imagePath, System.Drawing.Color.Green);
        using var services = new ServiceCollection()
            .AddPointframeDataServices($"Data Source={databasePath};Pooling=False")
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ICaptureCatalogService, CaptureCatalogService>()
            .AddSingleton<ICaptureLibrarySources>(new TestCaptureLibrarySources(libraryPath))
            .AddSingleton<ICaptureImportService, CaptureImportService>()
            .BuildServiceProvider();
        var sut = services.GetRequiredService<ICaptureImportService>();

        await sut.RequestReconciliationAsync();

        await using (var context = CreateContext(databasePath))
        {
            var artifact = await context.CaptureArtifacts.SingleAsync();
            Assert.Equal(CaptureArtifactSource.Import, artifact.Source);
            Assert.Equal(CaptureArtifactAvailability.Available, artifact.Availability);
        }

        File.Delete(imagePath);
        await sut.RequestReconciliationAsync();

        await using var deletedContext = CreateContext(databasePath);
        var deletedArtifact = await deletedContext.CaptureArtifacts.SingleAsync();
        Assert.Equal(CaptureArtifactAvailability.Missing, deletedArtifact.Availability);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static CaptureArtifactEntry CreateArtifact(
        string artifactId,
        CaptureArtifactAvailability availability,
        CaptureOcrStatus ocrStatus,
        DateTime? nextAttemptUtc)
    {
        return new CaptureArtifactEntry
        {
            ArtifactId = artifactId,
            MimeType = "image/png",
            FileName = $"{artifactId}.png",
            Sha256 = artifactId,
            CapturedAtUtc = DateTime.UnixEpoch,
            TimestampSource = CaptureTimestampSource.Capture,
            Source = CaptureArtifactSource.Import,
            Availability = availability,
            OcrStatus = ocrStatus,
            NextAttemptUtc = nextAttemptUtc,
            CreatedAtUtc = DateTime.UnixEpoch,
            UpdatedAtUtc = DateTime.UnixEpoch,
        };
    }

    private static PointframeDataContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<PointframeDataContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        return new PointframeDataContext(options);
    }

    private static void WritePng(string path, System.Drawing.Color color)
    {
        using var bitmap = new System.Drawing.Bitmap(2, 2);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(color);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private sealed class TestCaptureLibrarySources : ICaptureLibrarySources
    {
        private readonly string _root;

        public TestCaptureLibrarySources(string root)
        {
            _root = root;
        }

        public IReadOnlyList<string> GetImportRoots() => [_root];
    }
}
