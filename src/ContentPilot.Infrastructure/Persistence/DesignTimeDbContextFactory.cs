using ContentPilot.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ContentPilot.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` at design time. It never runs in a host, so it deliberately
/// uses an empty tenant context: migrations describe schema, not data.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=contentpilot;Username=contentpilot;Password=contentpilot";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new DesignTimeTenantContext(), new SystemClock());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;

        public bool IsCrossTenantScope => true;

        public Guid RequireTenantId() =>
            throw new InvalidOperationException("Design-time context has no tenant.");
    }
}
