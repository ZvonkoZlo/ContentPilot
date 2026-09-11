using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// A snapshot of one template manifest, taken from the Renderer service and pinned by every
/// <see cref="CreativeSpec"/> built against it. Global and shared across tenants — like
/// <c>PromptVersion</c>, it names a fact about the platform, not about any one brand.
/// <para>
/// The reason this exists at all, rather than a spec simply citing a template id and
/// version number: the renderer's manifest can be redesigned, and old renders must stay
/// explainable after that happens. A spec references this row, not the live manifest, so
/// re-reading last month's campaign never depends on the renderer still agreeing with
/// itself.
/// </para>
/// </summary>
public sealed class TemplateVersion : Entity, IAppendOnly
{
    private TemplateVersion()
    {
        TemplateId = null!;
        ManifestJson = null!;
        ContentHash = null!;
    }

    public TemplateVersion(
        string templateId,
        int version,
        string manifestJson,
        string contentHash,
        DateTimeOffset fetchedAt)
    {
        TemplateId = Guard.MaxLength(Guard.NotBlank(templateId), 80);
        Version = Guard.InRange(version, 1, int.MaxValue);
        ManifestJson = Guard.NotBlank(manifestJson);
        ContentHash = Guard.MaxLength(Guard.NotBlank(contentHash), 64);
        FetchedAt = fetchedAt;
    }

    public string TemplateId { get; private set; }

    public int Version { get; private set; }

    /// <summary>The manifest exactly as the renderer returned it, for full reproducibility.</summary>
    public string ManifestJson { get; private set; }

    /// <summary>SHA-256 of <see cref="ManifestJson"/>. Same manifest, same hash, no re-fetch needed.</summary>
    public string ContentHash { get; private set; }

    /// <summary>When this snapshot was taken from the renderer, not when the template itself changed.</summary>
    public DateTimeOffset FetchedAt { get; private set; }
}
