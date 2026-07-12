using System.IO;
using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Context;
using Pointframe.Data.Entities;
using Pointframe.Data.Repository;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SqliteCaptureTextCacheRepositoryTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Pointframe.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpsertAndTryGet_RoundTrip_ReturnsPersistedValue()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "cache.db");
        await using var context = CreateContext(databasePath);
        var sut = new CaptureTextCacheRepository(context);
        await sut.Add(new CaptureTextCacheEntry
        {
            FilePath = "a.png",
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Text = "hello",
            LastAccessedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var result = await sut.GetByFilePath("a.png");

        Assert.NotNull(result);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task TryGet_WithStaleCapturedAt_ReturnsNotFound()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "cache.db");
        await using var context = CreateContext(databasePath);
        var sut = new CaptureTextCacheRepository(context);
        await sut.Add(new CaptureTextCacheEntry
        {
            FilePath = "a.png",
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Text = "hello",
            LastAccessedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var result = await sut.GetByFilePath("missing.png");

        Assert.Null(result);
    }

    [Fact]
    public async Task TrimToLimitAsync_RemovesLeastRecentlyAccessedRows()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "cache.db");
        await using var context = CreateContext(databasePath);
        var sut = new CaptureTextCacheRepository(context);

        await sut.Add(new CaptureTextCacheEntry
        {
            FilePath = "a.png",
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Text = "a",
            LastAccessedAt = DateTime.UtcNow.AddMinutes(-3),
        });
        await sut.Add(new CaptureTextCacheEntry
        {
            FilePath = "b.png",
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Text = "b",
            LastAccessedAt = DateTime.UtcNow.AddMinutes(-2),
        });
        await sut.Add(new CaptureTextCacheEntry
        {
            FilePath = "c.png",
            CapturedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Text = "c",
            LastAccessedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        await context.SaveChangesAsync();

        await sut.TrimToLimit(2);
        await context.SaveChangesAsync();

        var all = await context.CaptureTextCacheEntries.OrderBy(entry => entry.FilePath).ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, entry => entry.FilePath == "a.png");
        Assert.Contains(all, entry => entry.FilePath == "b.png");
        Assert.Contains(all, entry => entry.FilePath == "c.png");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static PointframeDataContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<PointframeDataContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var context = new PointframeDataContext(options);
        context.Database.Migrate();
        return context;
    }
}
