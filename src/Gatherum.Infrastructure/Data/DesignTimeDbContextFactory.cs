using Gatherum.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Gatherum.Infrastructure.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GatherumDbContext>
{
    public GatherumDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Gatherum__Database__ConnectionString")
            ?? "Host=localhost;Database=gatherum;Username=gatherum;Password=gatherum";
        var options = new DbContextOptionsBuilder<GatherumDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly("Gatherum.Infrastructure"))
            .Options;
        return new GatherumDbContext(options);
    }
}
