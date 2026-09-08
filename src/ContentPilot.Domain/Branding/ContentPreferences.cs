using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// The weekly quota and the boundaries around it. Relational because it is validated
/// against numerically: the strategist's output is rejected outright if the counts do not
/// match, which is cheaper and more reliable than asking a model to count.
/// </summary>
public sealed class ContentPreferences : Entity, ITenantOwned, IAuditable
{
    private readonly List<string> _excludedTopics = [];
    private readonly List<string> _preferredTopics = [];
    private readonly List<DayOfWeek> _publishDays = [];

    private ContentPreferences()
    {
    }

    public ContentPreferences(Guid tenantId, Guid brandId)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        _publishDays.AddRange([DayOfWeek.Tuesday, DayOfWeek.Thursday]);
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    public int PostsPerWeek { get; private set; } = 2;

    public int CarouselsPerWeek { get; private set; } = 1;

    public int ReelsPerWeek { get; private set; } = 1;

    /// <summary>Hard exclusions. Checked literally after the strategist returns.</summary>
    public IReadOnlyList<string> ExcludedTopics => _excludedTopics;

    /// <summary>Suggestions, not instructions — the strategist may still choose otherwise.</summary>
    public IReadOnlyList<string> PreferredTopics => _preferredTopics;

    public IReadOnlyList<DayOfWeek> PublishDays => _publishDays;

    /// <summary>
    /// When the week is generated, in the brand's own timezone. Monday morning by default,
    /// so a week of content is waiting before anyone opens the app.
    /// </summary>
    public DayOfWeek GenerationDay { get; private set; } = DayOfWeek.Monday;

    public TimeOnly GenerationTime { get; private set; } = new(6, 0);

    public bool ScheduledGenerationEnabled { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public int TotalItemsPerWeek => PostsPerWeek + CarouselsPerWeek + ReelsPerWeek;

    public void SetQuota(int posts, int carousels, int reels)
    {
        // A week nobody can review is a week nobody publishes. The ceiling is a product
        // decision, not a technical one.
        PostsPerWeek = Guard.InRange(posts, 0, 14);
        CarouselsPerWeek = Guard.InRange(carousels, 0, 7);
        ReelsPerWeek = Guard.InRange(reels, 0, 7);

        if (TotalItemsPerWeek == 0)
        {
            throw new ArgumentException("A week with no content is not a schedule.", nameof(posts));
        }
    }

    public void SetExcludedTopics(IEnumerable<string> topics) => Replace(_excludedTopics, topics);

    public void SetPreferredTopics(IEnumerable<string> topics) => Replace(_preferredTopics, topics);

    public void SetPublishDays(IEnumerable<DayOfWeek> days)
    {
        var distinct = days.Distinct().OrderBy(d => d).ToList();

        if (distinct.Count == 0)
        {
            throw new ArgumentException("At least one publish day is required.", nameof(days));
        }

        _publishDays.Clear();
        _publishDays.AddRange(distinct);
    }

    public void SetSchedule(DayOfWeek day, TimeOnly time, bool enabled)
    {
        GenerationDay = day;
        GenerationTime = time;
        ScheduledGenerationEnabled = enabled;
    }

    private static void Replace(List<string> target, IEnumerable<string> values)
    {
        target.Clear();
        target.AddRange(values
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct()
            .Take(50));
    }
}
