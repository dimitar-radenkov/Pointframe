using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointframe.Data.Context;

namespace Pointframe.Data.Services;

public static class StaleLockSafeMigrator
{
    private const string LockTable = "__EFMigrationsLock";
    // A desktop app's migrations take seconds; a lock row older than this was left by a process that stopped
    // mid-migration. A younger row belongs to a live migration, so it is waited for rather than removed.
    private static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(60);

    public static async Task MigrateAsync(
        PointframeDataContext context,
        ILogger? logger = null,
        TimeSpan? staleAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await ClearStaleLockAsync(context, logger, staleAfter ?? DefaultStaleAfter, cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ClearStaleLockAsync(
        PointframeDataContext context,
        ILogger? logger,
        TimeSpan staleAfter,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var waiting = false;
            while (await LockTimestampAsync(connection, cancellationToken).ConfigureAwait(false) is { } taken)
            {
                if (DateTimeOffset.UtcNow - taken >= staleAfter)
                {
                    await using var delete = connection.CreateCommand();
                    delete.CommandText = $"DELETE FROM \"{LockTable}\"";
                    var removed = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    logger?.LogWarning("Cleared {Count} stale migration lock row(s) taken at {Taken:o} by a process that stopped during a migration", removed, taken);
                    return;
                }

                if (!waiting)
                {
                    logger?.LogInformation("The migration lock was taken at {Taken:o}; waiting for its owner to release it", taken);
                    waiting = true;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    // The lock row's timestamp, or null when no lock is held. An unreadable timestamp counts as stale.
    private static async Task<DateTimeOffset?> LockTimestampAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (!await LockRowExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using var read = connection.CreateCommand();
        read.CommandText = $"SELECT \"Timestamp\" FROM \"{LockTable}\" LIMIT 1";
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var taken)
            ? taken
            : DateTimeOffset.MinValue;
    }

    private static async Task<bool> LockRowExistsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var tableCheck = connection.CreateCommand();
        tableCheck.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{LockTable}'";
        if (Convert.ToInt64(await tableCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
        {
            return false;
        }

        await using var rows = connection.CreateCommand();
        rows.CommandText = $"SELECT COUNT(*) FROM \"{LockTable}\"";
        return Convert.ToInt64(await rows.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
    }
}
