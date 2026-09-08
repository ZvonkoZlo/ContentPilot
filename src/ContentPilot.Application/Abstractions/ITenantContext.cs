namespace ContentPilot.Application.Abstractions;

/// <summary>
/// The ambient tenant for the current request or job. Resolved once at the edge, then
/// consumed by the DbContext global query filters — never passed around by hand and
/// never derived from user input inside a use case.
/// </summary>
public interface ITenantContext
{
    /// <summary>The active tenant, or null when running outside any tenant scope.</summary>
    Guid? TenantId { get; }

    /// <summary>
    /// True while an explicitly authorised maintenance operation needs to cross tenant
    /// boundaries (migrations, the retention reaper, the scheduling reconciler).
    /// </summary>
    bool IsCrossTenantScope { get; }

    Guid RequireTenantId();
}

/// <summary>
/// A tenant context whose value can be set for the lifetime of a scope. Jobs resolve
/// their tenant from the job row; HTTP requests resolve it from the authenticated user.
/// </summary>
public interface IMutableTenantContext : ITenantContext
{
    void SetTenant(Guid tenantId);

    /// <summary>
    /// Opens an explicitly authorised cross-tenant scope. Disposing restores the
    /// previous state, so a maintenance job cannot leak an unfiltered context.
    /// </summary>
    IDisposable BeginCrossTenantScope();
}

public sealed class TenantContext : IMutableTenantContext
{
    public Guid? TenantId { get; private set; }

    public bool IsCrossTenantScope { get; private set; }

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant identifier must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public Guid RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException(
            "No tenant is in scope. Resolve a tenant at the edge before touching tenant-owned data.");

    public IDisposable BeginCrossTenantScope()
    {
        var previousTenant = TenantId;
        var previousFlag = IsCrossTenantScope;
        IsCrossTenantScope = true;

        return new Restore(() =>
        {
            TenantId = previousTenant;
            IsCrossTenantScope = previousFlag;
        });
    }

    private sealed class Restore(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            onDispose();
        }
    }
}
