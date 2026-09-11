namespace ContentPilot.Application.Campaigns;

/// <summary>
/// The one place that knows what "this week" means for a campaign. A campaign's own week
/// always starts on a Monday, so both the manual trigger and the scheduled ones need the
/// same arithmetic — duplicating it is how the two quietly start disagreeing about which
/// week "now" belongs to.
/// </summary>
public static class CampaignWeek
{
    /// <summary>The nearest Monday at or after <paramref name="date"/>.</summary>
    public static DateOnly MondayOnOrAfter(DateOnly date)
    {
        var offset = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;

        return date.AddDays(offset);
    }

    /// <summary>The Monday that starts the week <paramref name="date"/> falls in.</summary>
    public static DateOnly MondayOnOrBefore(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;

        return date.AddDays(-offset);
    }
}
