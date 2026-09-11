using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Packaging;

/// <summary>
/// The 1–5 "would I publish this" rating from Phase 8's review UI. One row per item — a
/// second rating replaces the first rather than accumulating a history, since what matters
/// is the reviewer's current verdict, not a log of how it changed.
/// </summary>
public sealed class HumanRating : Entity, ITenantOwned
{
    private HumanRating()
    {
    }

    public HumanRating(Guid tenantId, Guid contentItemId, int score, DateTimeOffset ratedAt, string? note = null)
    {
        TenantId = Guard.NotEmpty(tenantId);
        ContentItemId = Guard.NotEmpty(contentItemId);
        Score = Guard.InRange(score, 1, 5);
        Note = note is null ? null : Guard.MaxLength(note, 1000);
        RatedAt = ratedAt;
    }

    public Guid TenantId { get; private set; }

    public Guid ContentItemId { get; private set; }

    public int Score { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset RatedAt { get; private set; }

    public void Update(int score, DateTimeOffset ratedAt, string? note = null)
    {
        Score = Guard.InRange(score, 1, 5);
        Note = note is null ? null : Guard.MaxLength(note, 1000);
        RatedAt = ratedAt;
    }
}
