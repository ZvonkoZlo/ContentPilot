using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Packaging;

/// <summary>
/// The built form of a campaign: <c>plan.json</c>, <c>manifest.json</c>, and every asset
/// laid out under <c>campaigns/{campaignId}/...</c> in object storage. One row per campaign
/// — unlike <see cref="Content.ContentRevision"/> or <see cref="Quality.QualityReview"/>,
/// this is not append-only, because approving an item after the fact rebuilds the same
/// package in place rather than producing a new historical one.
/// </summary>
public sealed class CampaignPackage : Entity, ITenantOwned
{
    private CampaignPackage()
    {
        ManifestJson = null!;
    }

    public CampaignPackage(Guid tenantId, Guid campaignId, string manifestJson, DateTimeOffset builtAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        CampaignId = Guard.NotEmpty(campaignId);
        ManifestJson = Guard.NotBlank(manifestJson);
        BuiltAt = builtAt;
    }

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; private set; }

    /// <summary>Every file the build produced: path, sha256, bytes, content type.</summary>
    public string ManifestJson { get; private set; }

    /// <summary>Set once the ZIP exists; streaming build is a separate, later step.</summary>
    public string? ZipKey { get; private set; }

    public DateTimeOffset BuiltAt { get; private set; }

    /// <summary>Recorded on the packaging step so a job retry cannot double-send the email.</summary>
    public DateTimeOffset? EmailSentAt { get; private set; }

    public void Rebuild(string manifestJson, DateTimeOffset builtAt)
    {
        ManifestJson = Guard.NotBlank(manifestJson);
        BuiltAt = builtAt;

        // A rebuild invalidates any ZIP already produced against the old manifest; a new
        // one is built lazily the next time someone downloads it, not eagerly here.
        ZipKey = null;
    }

    public void SetZipKey(string zipKey) => ZipKey = Guard.NotBlank(zipKey);

    public void MarkEmailSent(DateTimeOffset now) => EmailSentAt ??= now;
}
