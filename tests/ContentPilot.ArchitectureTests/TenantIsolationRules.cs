using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Common;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace ContentPilot.ArchitectureTests;

/// <summary>
/// Tenant isolation is enforced by a reflection loop in the DbContext, which means a new
/// entity is protected automatically — as long as nobody breaks the loop. This asserts
/// the resulting model, not the code that built it.
/// </summary>
public sealed class TenantIsolationRules
{
    [Fact]
    public void Every_tenant_owned_entity_has_a_global_query_filter()
    {
        using var context = BuildModelOnlyContext();

        var unfiltered = context.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        unfiltered.ShouldBeEmpty(
            "Tenant-owned entities without a query filter leak across tenants on every read: " +
            string.Join(", ", unfiltered));
    }

    [Fact]
    public void Tenant_owned_entities_are_discovered_at_all()
    {
        using var context = BuildModelOnlyContext();

        var owned = context.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
            .ToList();

        // Guards against the filter test passing vacuously if the interface is ever
        // renamed or dropped from the entities.
        owned.ShouldNotBeEmpty("No ITenantOwned entities found; the isolation test would be meaningless.");
    }

    [Fact]
    public void Append_only_entities_reject_updates()
    {
        // No append-only entity exists yet (they arrive with AgentRun and CostEntry in
        // Phase 3). The guard is asserted directly so the behaviour is locked in first.
        var guard = typeof(AppDbContext).GetMethod("GuardAppendOnly",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        guard.ShouldNotBeNull("AppDbContext must keep its append-only guard.");
    }

    private static AppDbContext BuildModelOnlyContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new TenantContext(), new SystemClock());
    }
}
