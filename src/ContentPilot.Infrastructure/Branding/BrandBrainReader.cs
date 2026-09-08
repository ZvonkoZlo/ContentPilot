using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Brand;
using ContentPilot.Application.Capabilities;
using ContentPilot.Domain.Branding;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Branding;

/// <summary>
/// Loads a brand's scattered rows, composes them, and freezes the result on demand.
/// <para>
/// Every query here is tenant-filtered by the DbContext, so a brand identifier from another
/// tenant simply does not resolve — the failure is "no such brand", which is also the
/// correct thing to tell a caller who should not know it exists.
/// </para>
/// </summary>
public sealed class BrandBrainReader(AppDbContext db, IClock clock) : IBrandBrainReader
{
    public async Task<BrandSnapshot> GetSnapshotAsync(Guid brandId, CancellationToken ct = default)
    {
        var input = await LoadAsync(brandId, ct);

        return BrandBrainAssembler.Assemble(input, clock.UtcNow);
    }

    public async Task<BrandVersionRef> CaptureVersionAsync(Guid brandId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(brandId, ct);
        var hash = snapshot.ComputeHash();

        var existing = await db.BrandProfileVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.BrandId == brandId && v.ContentHash == hash, ct);

        if (existing is not null)
        {
            // The brand has not changed since the last campaign. Reusing the version keeps
            // the history a record of real changes rather than of how often we ran.
            return new BrandVersionRef(existing.Id, hash, WasCreated: false, snapshot);
        }

        var version = new BrandProfileVersion(
            db.CurrentTenantId ?? throw new InvalidOperationException("No tenant in scope."),
            brandId,
            snapshot.ToJson(),
            hash,
            clock.UtcNow);

        db.BrandProfileVersions.Add(version);
        await db.SaveChangesAsync(ct);

        return new BrandVersionRef(version.Id, hash, WasCreated: true, snapshot);
    }

    public async Task<BrandSnapshot> GetVersionAsync(Guid versionId, CancellationToken ct = default)
    {
        var version = await db.BrandProfileVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new InvalidOperationException($"No brand version '{versionId}' in the tenant in scope.");

        return BrandSnapshot.FromJson(version.SnapshotJson);
    }

    private async Task<BrandBrainAssembler.Input> LoadAsync(Guid brandId, CancellationToken ct)
    {
        var brand = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == brandId, ct)
            ?? throw new BrandNotFoundException(brandId);

        // Five small reads rather than one join: the rows are few, the shapes differ, and
        // a single query would fan out into a cartesian product across four collections.
        var profile = await db.BrandProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.BrandId == brandId, ct);
        var preferences = await db.ContentPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.BrandId == brandId, ct);
        var facts = await db.ProductFacts.AsNoTracking().Where(f => f.BrandId == brandId).ToListAsync(ct);
        var personas = await db.AudiencePersonas.AsNoTracking().Where(p => p.BrandId == brandId).ToListAsync(ct);

        var assets = await db.BrandAssets.AsNoTracking()
            .Where(a => a.BrandId == brandId && !a.IsArchived)
            .ToListAsync(ct);

        return new BrandBrainAssembler.Input(brand, profile, facts, personas, preferences, assets);
    }
}
