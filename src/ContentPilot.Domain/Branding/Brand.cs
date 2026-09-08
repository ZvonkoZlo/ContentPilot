using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// A business the platform produces content for. A tenant may eventually own several;
/// the MVP exposes one. Nothing here is industry-specific by design.
/// </summary>
public sealed class Brand : Entity, ITenantOwned, IAuditable
{
    private readonly List<string> _languages = [];

    private Brand()
    {
        Name = null!;
        TimeZoneId = null!;
    }

    public Brand(Guid tenantId, string name, string timeZoneId, IEnumerable<string>? languages = null, string? website = null)
    {
        TenantId = Guard.NotEmpty(tenantId);
        Name = Guard.MaxLength(Guard.NotBlank(name), 200);
        TimeZoneId = Guard.NotBlank(timeZoneId);
        Website = string.IsNullOrWhiteSpace(website) ? null : Guard.MaxLength(website.Trim(), 500);
        _languages.AddRange(languages?.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).Distinct() ?? ["en"]);

        if (_languages.Count == 0)
        {
            _languages.Add("en");
        }
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string? Website { get; private set; }

    /// <summary>IANA identifier. Weekly generation fires at 06:00 local to the brand.</summary>
    public string TimeZoneId { get; private set; }

    /// <summary>BCP-47 language tags the copywriter may produce. First entry is primary.</summary>
    public IReadOnlyList<string> Languages => _languages;

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public string PrimaryLanguage => _languages[0];

    public void Rename(string name) => Name = Guard.MaxLength(Guard.NotBlank(name), 200);

    public void SetWebsite(string? website) =>
        Website = string.IsNullOrWhiteSpace(website) ? null : Guard.MaxLength(website.Trim(), 500);

    public void SetTimeZone(string timeZoneId) => TimeZoneId = Guard.NotBlank(timeZoneId);

    public void SetLanguages(IEnumerable<string> languages)
    {
        var normalised = languages
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();

        if (normalised.Count == 0)
        {
            throw new ArgumentException("A brand needs at least one language.", nameof(languages));
        }

        _languages.Clear();
        _languages.AddRange(normalised);
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
