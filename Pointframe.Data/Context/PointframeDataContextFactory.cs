using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pointframe.Data.Context;

internal sealed class PointframeDataContextFactory : IDesignTimeDbContextFactory<PointframeDataContext>
{
    public PointframeDataContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PointframeDataContext>();
        optionsBuilder.UseSqlite("Data Source=pointframe.db");

        return new PointframeDataContext(optionsBuilder.Options);
    }
}
