using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// Who the content is for. Both the strategist and the marketing QA agent read these — one
/// to choose what is worth saying, the other to check the copy actually speaks to them.
/// <para>
/// The pains and the vocabulary carry the weight. "Salon owners aged 25-45" tells an agent
/// nothing it can write from; "loses two bookings a week to unanswered DMs" does.
/// </para>
/// </summary>
public sealed class AudiencePersona : Entity, ITenantOwned, IAuditable
{
    private AudiencePersona()
    {
        Name = null!;
        Detail = null!;
    }

    public AudiencePersona(Guid tenantId, Guid brandId, string name, string segment, PersonaDetail detail)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        Name = Guard.MaxLength(Guard.NotBlank(name), 120);
        Segment = Guard.MaxLength(Guard.NotBlank(segment), 160);
        Detail = detail;
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    /// <summary>A person, not a category: "Ana, owner of a two-chair salon".</summary>
    public string Name { get; private set; }

    /// <summary>The market slice: "independent beauty salons, 1-5 staff".</summary>
    public string Segment { get; private set; } = string.Empty;

    public PersonaDetail Detail { get; private set; }

    /// <summary>Exactly one persona is primary; the strategist defaults to it.</summary>
    public bool IsPrimary { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public void MakePrimary() => IsPrimary = true;

    public void ClearPrimary() => IsPrimary = false;

    public void Update(string name, string segment, PersonaDetail detail)
    {
        Name = Guard.MaxLength(Guard.NotBlank(name), 120);
        Segment = Guard.MaxLength(Guard.NotBlank(segment), 160);
        Detail = detail;
    }
}

public sealed record PersonaDetail
{
    /// <summary>What actually costs them time or money today.</summary>
    public IReadOnlyList<string> Pains { get; init; } = [];

    public IReadOnlyList<string> Goals { get; init; } = [];

    /// <summary>Why they would not buy. The richest source of content there is.</summary>
    public IReadOnlyList<string> Objections { get; init; } = [];

    /// <summary>Their words, not the brand's. Copy that uses these reads as written from inside.</summary>
    public IReadOnlyList<string> Vocabulary { get; init; } = [];

    public string? Context { get; init; }
}
