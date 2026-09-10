using System.Linq.Expressions;
using System.Reflection;
using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Observability;
using ContentPilot.Domain.Quality;
using ContentPilot.Domain.Common;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ContentPilot.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext, IClock clock)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();

    public DbSet<BrandProfileVersion> BrandProfileVersions => Set<BrandProfileVersion>();

    public DbSet<BrandAsset> BrandAssets => Set<BrandAsset>();

    public DbSet<ProductFact> ProductFacts => Set<ProductFact>();

    public DbSet<AudiencePersona> AudiencePersonas => Set<AudiencePersona>();

    public DbSet<ContentPreferences> ContentPreferences => Set<ContentPreferences>();

    /// <summary>Shared reference data, not tenant-owned: onboarding seed material.</summary>
    public DbSet<IndustryProfile> IndustryProfiles => Set<IndustryProfile>();

    public DbSet<ContentCampaign> ContentCampaigns => Set<ContentCampaign>();

    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    public DbSet<ContentHistoryEntry> ContentHistory => Set<ContentHistoryEntry>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<CostEntry> CostEntries => Set<CostEntry>();

    /// <summary>Immutable prompt text, shared across tenants and referenced by every run.</summary>
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();

    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>One row per QA gate per attempt, deterministic gates included.</summary>
    public DbSet<QualityReview> QualityReviews => Set<QualityReview>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>
    /// Every <see cref="ITenantOwned"/> entity gets its filter here, by reflection, so
    /// adding an entity cannot silently skip isolation. An architecture test asserts the
    /// resulting model has no unfiltered tenant-owned type.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantIdProperty = Expression.Property(parameter, nameof(ITenantOwned.TenantId));

            // e => this.IsCrossTenantScope || e.TenantId == this.CurrentTenantId
            // Both context members are read per query, so a scope change is picked up
            // without rebuilding the model.
            var crossTenant = Expression.Property(Expression.Constant(this), nameof(IsCrossTenantScope));
            var currentTenant = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));

            var matches = Expression.Equal(
                Expression.Convert(tenantIdProperty, typeof(Guid?)),
                currentTenant);

            var body = Expression.OrElse(crossTenant, matches);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    /// <summary>Read by the compiled query filters. Do not inline.</summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    /// <summary>Read by the compiled query filters. Do not inline.</summary>
    public bool IsCrossTenantScope => tenantContext.IsCrossTenantScope;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditTimestamps();
        GuardAppendOnly();
        GuardTenantAssignment();

        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditTimestamps();
        GuardAppendOnly();
        GuardTenantAssignment();

        return base.SaveChanges();
    }

    private void ApplyAuditTimestamps()
    {
        var now = clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = null;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(nameof(IAuditable.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Append-only tables are the audit trail. Losing a row to a stray update is losing
    /// the ability to explain what the system did, so the write is refused outright.
    /// </summary>
    private void GuardAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not IAppendOnly)
            {
                continue;
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Entity.GetType().Name} is append-only and cannot be {entry.State.ToString().ToLowerInvariant()}.");
            }
        }
    }

    /// <summary>
    /// Stamps the ambient tenant onto new rows and refuses any write that would place a
    /// row in a tenant other than the one in scope.
    /// </summary>
    private void GuardTenantAssignment()
    {
        if (tenantContext.IsCrossTenantScope)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenantOwned owned)
            {
                continue;
            }

            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var scoped = tenantContext.TenantId;

            if (scoped is null)
            {
                throw new InvalidOperationException(
                    $"Cannot persist {entry.Entity.GetType().Name}: no tenant is in scope.");
            }

            if (owned.TenantId == Guid.Empty)
            {
                SetTenantId(entry, scoped.Value);
                continue;
            }

            if (owned.TenantId != scoped.Value)
            {
                throw new InvalidOperationException(
                    $"Cannot persist {entry.Entity.GetType().Name} belonging to tenant {owned.TenantId} while tenant {scoped} is in scope.");
            }
        }
    }

    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        if (Database.CurrentTransaction is not null)
        {
            return await action(ct);
        }

        await using var transaction = await Database.BeginTransactionAsync(ct);
        var result = await action(ct);
        await transaction.CommitAsync(ct);

        return result;
    }

    private static void SetTenantId(EntityEntry entry, Guid tenantId) =>
        entry.Property(nameof(ITenantOwned.TenantId)).CurrentValue = tenantId;
}
