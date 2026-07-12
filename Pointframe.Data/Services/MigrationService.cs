using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Context;

namespace Pointframe.Data.Services;

internal sealed class MigrationService : IMigrationService
{
    private readonly PointframeDataContext _context;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(
        PointframeDataContext context,
        ILogger<MigrationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task ApplyMigrations()
    {
        _logger.LogDebug("Checking for pending database migrations...");

        var pendingMigrations = await _context.Database.GetPendingMigrationsAsync();
        var pendingList = pendingMigrations.ToList();

        if (pendingList.Count == 0)
        {
            _logger.LogInformation("No pending migrations found. Database is up to date.");
            return;
        }

        _logger.LogInformation(
            "Found {Count} pending migrations: {Migrations}",
            pendingList.Count,
            string.Join(", ", pendingList));

        _logger.LogDebug("Applying database migrations...");
        await _context.Database.MigrateAsync();

        _logger.LogDebug("Database migrations applied successfully.");
    }
}
