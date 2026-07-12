using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data.Abstractions;
using Pointframe.Data.Context;
using Pointframe.Data.Repository;
using Pointframe.Data.Services;

namespace Pointframe.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddPointframeDataServices(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<PointframeDataContext>(options =>
        {
            options.UseSqlite(connectionString);
        });

        services.AddScoped<IPointframeDataUnitOfWork, PointframeDataUnitOfWork>();
        services.AddScoped<ICaptureTextCacheRepository, CaptureTextCacheRepository>();
        services.AddScoped<IMigrationService, MigrationService>();
        return services;
    }
}
