using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Context;
using Pointframe.Data.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class StaleLockSafeMigratorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Pointframe.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MigrateAsync_StaleLockRow_ClearsItAndMigrates()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "stale.db");
        await using (var seed = CreateContext(databasePath))
        {
            await seed.Database.MigrateAsync();
        }

        ExecuteNonQuery(databasePath, "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-09-19 00:00:00+00:00')");

        await using var context = CreateContext(databasePath);
        var migrate = StaleLockSafeMigrator.MigrateAsync(context, staleAfter: TimeSpan.FromSeconds(60));
        var finished = await Task.WhenAny(migrate, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(migrate, finished);
        await migrate;
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(0L, ExecuteScalar(databasePath, "SELECT COUNT(*) FROM \"__EFMigrationsLock\""));
    }

    [Fact]
    public async Task MigrateAsync_LockReleasedByOwner_DoesNotClearItEarly()
    {
        Directory.CreateDirectory(_tempDirectory);
        var databasePath = Path.Combine(_tempDirectory, "owned.db");
        await using (var seed = CreateContext(databasePath))
        {
            await seed.Database.MigrateAsync();
        }

        // A fresh lock belongs to a live migration: it must be waited for, never removed.
        var takenNow = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture);
        ExecuteNonQuery(databasePath, $"INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '{takenNow}')");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var release = Task.Run(async () =>
        {
            await Task.Delay(400);
            ExecuteNonQuery(databasePath, "DELETE FROM \"__EFMigrationsLock\"");
        });

        await using var context = CreateContext(databasePath);
        await StaleLockSafeMigrator.MigrateAsync(context, staleAfter: TimeSpan.FromSeconds(60));
        await release;
        Assert.True(stopwatch.ElapsedMilliseconds >= 350, $"The migrator did not wait for the owner ({stopwatch.ElapsedMilliseconds} ms).");

        Assert.Equal(0L, ExecuteScalar(databasePath, "SELECT COUNT(*) FROM \"__EFMigrationsLock\""));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
        return new PointframeDataContext(options);
    }

    private static void ExecuteNonQuery(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
