using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// The exact render input for one attempt, immutable once written. This is what
/// SpecAssembly produces and Rendering consumes — pinning it, rather than re-deriving a
/// <c>RenderImageRequest</c> from live copy and a live template each time, is what makes
/// re-rendering a past attempt reproduce the same asset (modulo a generated background,
/// which is referenced by asset id rather than regenerated).
/// </summary>
public sealed class CreativeSpec : Entity, ITenantOwned, IAppendOnly
{
    private CreativeSpec()
    {
        SpecJson = null!;
        SpecHash = null!;
    }

    public CreativeSpec(
        Guid tenantId,
        Guid contentItemId,
        int attempt,
        Guid templateVersionId,
        string specJson,
        string specHash,
        DateTimeOffset createdAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        ContentItemId = Guard.NotEmpty(contentItemId);
        Attempt = Guard.InRange(attempt, 1, 1000);
        TemplateVersionId = Guard.NotEmpty(templateVersionId);
        SpecJson = Guard.NotBlank(specJson);
        SpecHash = Guard.MaxLength(Guard.NotBlank(specHash), 64);
        CreatedAt = createdAt;
    }

    public Guid TenantId { get; private set; }

    public Guid ContentItemId { get; private set; }

    /// <summary>One immutable spec per attempt — never revised, only superseded by the next attempt's row.</summary>
    public int Attempt { get; private set; }

    public Guid TemplateVersionId { get; private set; }

    /// <summary>The serialized <c>RenderImageRequest</c>, verbatim — what Rendering actually sends.</summary>
    public string SpecJson { get; private set; }

    /// <summary>SHA-256 of <see cref="SpecJson"/>. Two attempts with the same hash rendered the same input.</summary>
    public string SpecHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
