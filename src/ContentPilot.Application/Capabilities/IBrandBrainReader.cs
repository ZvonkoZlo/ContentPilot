using ContentPilot.Application.Brand;

namespace ContentPilot.Application.Capabilities;

/// <summary>
/// Read access to a brand, in the shape agents consume.
/// <para>
/// This namespace is the future MCP surface. Everything in it takes and returns plain
/// objects, carries no <c>DbContext</c> or <c>HttpContext</c> in its signature, and is
/// read-only — so exposing it over a protocol later is a thin façade rather than a
/// redesign. Writes stay off this surface deliberately: an agent that can save is an agent
/// that can decide what happens next.
/// </para>
/// </summary>
public interface IBrandBrainReader
{
    /// <summary>Composes the current state of the brand. Does not persist anything.</summary>
    Task<BrandSnapshot> GetSnapshotAsync(Guid brandId, CancellationToken ct = default);

    /// <summary>
    /// Freezes the current snapshot as an immutable version and returns it. An unchanged
    /// brand returns the existing version rather than creating an identical one, so the
    /// history stays meaningful instead of accumulating noise.
    /// </summary>
    Task<BrandVersionRef> CaptureVersionAsync(Guid brandId, CancellationToken ct = default);

    Task<BrandSnapshot> GetVersionAsync(Guid versionId, CancellationToken ct = default);
}

public sealed record BrandVersionRef(Guid VersionId, string ContentHash, bool WasCreated, BrandSnapshot Snapshot);

/// <summary>Thrown when a brand identifier does not resolve inside the tenant in scope.</summary>
public sealed class BrandNotFoundException(Guid brandId)
    : Exception($"No brand '{brandId}' in the tenant in scope.");
