using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TriageDesk.Api.Persistence;

public sealed class DesignTimeContext : IDesignTimeDbContextFactory<TriageDeskDbContext>
{
    public TriageDeskDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__TriageDesk")
            ?? throw new InvalidOperationException("Set ConnectionStrings__TriageDesk to the direct migration connection.");
        return new TriageDeskDbContext(new DbContextOptionsBuilder<TriageDeskDbContext>()
            .UseNpgsql(connection, postgres => postgres.SetPostgresVersion(18, 0)).Options);
    }
}
