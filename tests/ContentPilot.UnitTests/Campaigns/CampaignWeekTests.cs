using ContentPilot.Application.Campaigns;
using Shouldly;

namespace ContentPilot.UnitTests.Campaigns;

public sealed class CampaignWeekTests
{
    [Theory]
    [InlineData(2026, 9, 14, 2026, 9, 14)] // A Monday maps to itself.
    [InlineData(2026, 9, 15, 2026, 9, 21)] // Tuesday rolls forward to next Monday.
    [InlineData(2026, 9, 20, 2026, 9, 21)] // Sunday rolls forward too.
    public void MondayOnOrAfter_finds_the_next_monday_inclusive(int y, int m, int d, int ey, int em, int ed)
    {
        CampaignWeek.MondayOnOrAfter(new DateOnly(y, m, d)).ShouldBe(new DateOnly(ey, em, ed));
    }

    [Theory]
    [InlineData(2026, 9, 14, 2026, 9, 14)] // A Monday maps to itself.
    [InlineData(2026, 9, 15, 2026, 9, 14)] // Tuesday belongs to the Monday just past.
    [InlineData(2026, 9, 20, 2026, 9, 14)] // Sunday still belongs to that same week.
    public void MondayOnOrBefore_finds_the_week_the_date_belongs_to(int y, int m, int d, int ey, int em, int ed)
    {
        CampaignWeek.MondayOnOrBefore(new DateOnly(y, m, d)).ShouldBe(new DateOnly(ey, em, ed));
    }
}
