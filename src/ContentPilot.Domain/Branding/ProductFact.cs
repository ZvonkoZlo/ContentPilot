using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// One atomic, citable claim about the product.
/// <para>
/// This is the one part of the Brand Brain that must not be JSON. Every factual assertion
/// the copywriter makes has to cite a fact by identifier, and the marketing QA agent then
/// checks the cited fact actually supports the claim. A claim that cites nothing is a
/// hallucination the validator can reject without asking a model anything.
/// </para>
/// </summary>
public sealed class ProductFact : Entity, ITenantOwned, IAuditable
{
    private ProductFact()
    {
        Key = null!;
        Statement = null!;
    }

    public ProductFact(Guid tenantId, Guid brandId, string key, string statement, FactCategory category)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        Key = NormaliseKey(key);
        Statement = Guard.MaxLength(Guard.NotBlank(statement), 600);
        Category = category;
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    /// <summary>
    /// Stable, human-readable, unique per brand: <c>whatsapp-reminders</c>. Copy cites this,
    /// so renaming it breaks the trail back from a published post to what justified it.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>One sentence, in plain language, that is literally true.</summary>
    public string Statement { get; private set; }

    public FactCategory Category { get; private set; }

    /// <summary>Where the claim comes from: a screenshot, a pricing page, a measurement.</summary>
    public string? Evidence { get; private set; }

    /// <summary>False for facts that are true but not for public use — internal metrics, roadmap.</summary>
    public bool IsPublic { get; private set; } = true;

    /// <summary>
    /// Facts expire. A launch discount that ran in March must not be cited in June, and
    /// nothing but a date range prevents that.
    /// </summary>
    public DateTimeOffset? ValidFrom { get; private set; }

    public DateTimeOffset? ValidTo { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsUsableAt(DateTimeOffset moment) =>
        IsPublic
        && (ValidFrom is null || ValidFrom <= moment)
        && (ValidTo is null || ValidTo >= moment);

    public void Restate(string statement) => Statement = Guard.MaxLength(Guard.NotBlank(statement), 600);

    public void SetEvidence(string? evidence) =>
        Evidence = string.IsNullOrWhiteSpace(evidence) ? null : Guard.MaxLength(evidence.Trim(), 500);

    public void SetVisibility(bool isPublic) => IsPublic = isPublic;

    public void SetValidity(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not null && to is not null && to < from)
        {
            throw new ArgumentException("A fact cannot stop being true before it starts.", nameof(to));
        }

        ValidFrom = from;
        ValidTo = to;
    }

    private static string NormaliseKey(string key)
    {
        var value = Guard.MaxLength(Guard.NotBlank(key), 80).ToLowerInvariant();

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                throw new ArgumentException(
                    "A fact key may contain only lowercase letters, digits and hyphens.", nameof(key));
            }
        }

        return value;
    }
}

public enum FactCategory
{
    Feature = 0,
    Pricing = 1,
    Integration = 2,
    Availability = 3,
    Outcome = 4,
    Company = 5,
}
