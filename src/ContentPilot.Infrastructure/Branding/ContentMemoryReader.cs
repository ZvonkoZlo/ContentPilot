using ContentPilot.Application.Agents;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Branding;

/// <summary>
/// Reads what a brand has already published, in the shape the strategist needs.
/// <para>
/// It serves two purposes at once, and they want different amounts of data. The model needs
/// enough recent context to avoid covering the same ground; the novelty validator needs a
/// longer tail of hashes, because a topic repeated after three months is still a repeat and
/// hashes cost nothing to carry.
/// </para>
/// </summary>
public sealed class ContentMemoryReader(AppDbContext db)
{
    /// <summary>
    /// How many entries keep their text. Matches what the strategist actually shows; the
    /// agent decides how much to print, this decides how much to carry.
    /// </summary>
    public const int PromptWindow = ContentStrategistAgent.RecentContentShown;

    /// <summary>
    /// Six months of hashes for the novelty check. Long, because it is nearly free — a
    /// hash is eight bytes and the comparison is a popcount.
    /// </summary>
    public const int NoveltyWindowDays = 183;

    public async Task<IReadOnlyList<RecentContent>> ReadAsync(
        Guid brandId, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var since = asOf.AddDays(-NoveltyWindowDays);

        var entries = await db.ContentHistory
            .AsNoTracking()
            .Where(entry => entry.BrandId == brandId && entry.ApprovedAt >= since)
            .OrderByDescending(entry => entry.ApprovedAt)
            .Select(entry => new
            {
                entry.Topic,
                entry.Pillar,
                entry.TopicSimHash,
                entry.ApprovedAt,
                entry.HumanRating,
            })
            .ToListAsync(ct);

        return entries
            .Select((entry, index) => new RecentContent
            {
                // Beyond the prompt window the topic text is never shown to the model, so
                // it is dropped rather than carried: the hash is the only part still doing
                // work, and an unused string in a loop is a string somebody later prints.
                Topic = index < PromptWindow ? entry.Topic : string.Empty,
                Pillar = index < PromptWindow ? entry.Pillar : string.Empty,
                TopicHash = entry.TopicSimHash,
                ApprovedAt = entry.ApprovedAt,
                HumanRating = index < PromptWindow ? entry.HumanRating : null,
            })
            .ToArray();
    }
}
