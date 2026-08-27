using SocietyHub.Helpdesk.Api.Domain;

namespace SocietyHub.Helpdesk.Tests;

/// <summary>
/// The 24-hour promise is the product commitment residents judge the platform on, and the
/// naive implementation — <c>raisedAt.AddHours(24)</c> — is wrong in ways they notice
/// immediately. These pin the working-hours clock.
/// </summary>
public sealed class SlaPolicyTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    /// <summary>Builds a UTC instant from a wall-clock time in India.</summary>
    private static DateTimeOffset IndiaTime(int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, 9, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, India.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateTimeOffset DueFor(DateTimeOffset raisedUtc, ComplaintPriority priority) =>
        SlaPolicy.CalculateDueAt(raisedUtc, priority, India);

    private static (int Day, int Hour, int Minute) AsIndiaClock(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, India);
        return (local.Day, local.Hour, local.Minute);
    }

    [Fact]
    public void A_complaint_raised_mid_morning_consumes_the_working_day()
    {
        // Raised 09:00 on the 1st with 24 working hours. The day gives 11 hours (09:00–20:00),
        // the next gives 12 (08:00–20:00), leaving 1 hour on the third morning.
        var due = DueFor(IndiaTime(1, 9), ComplaintPriority.Normal);

        Assert.Equal((3, 9, 0), AsIndiaClock(due));
    }

    [Fact]
    public void A_complaint_raised_at_night_does_not_burn_its_budget_overnight()
    {
        // The bug this exists to prevent. Raised at 23:00, a naive AddHours(24) would be due
        // at 23:00 the next night, having spent nine hours while nobody was working — and a
        // plumber cannot be sent at 2am, so the society could not have met it even in theory.
        var raised = IndiaTime(1, 23);

        var naive = raised.AddHours(24);
        var actual = DueFor(raised, ComplaintPriority.Normal);

        Assert.True(actual > naive);

        // The clock starts at 08:00 the next morning and runs 24 working hours: 12 on the 2nd,
        // 12 on the 3rd, finishing at close of business.
        Assert.Equal((3, 20, 0), AsIndiaClock(actual));
    }

    [Fact]
    public void A_complaint_raised_before_opening_starts_at_opening()
    {
        var raised = IndiaTime(1, 5, 30);
        var due = DueFor(raised, ComplaintPriority.High);

        // Eight working hours from 08:00 is 16:00 the same day.
        Assert.Equal((1, 16, 0), AsIndiaClock(due));
    }

    [Fact]
    public void An_emergency_ignores_working_hours_entirely()
    {
        // Nobody trapped in a lift cares that it is 3am, and a two-hour promise measured in
        // working hours would silently mean "tomorrow morning".
        var raised = IndiaTime(1, 2);
        var due = DueFor(raised, ComplaintPriority.Emergency);

        Assert.Equal(raised.AddHours(2), due);
        Assert.Equal((1, 4, 0), AsIndiaClock(due));
    }

    [Fact]
    public void Priority_shortens_the_budget()
    {
        var raised = IndiaTime(1, 9);

        var low = DueFor(raised, ComplaintPriority.Low);
        var normal = DueFor(raised, ComplaintPriority.Normal);
        var high = DueFor(raised, ComplaintPriority.High);
        var emergency = DueFor(raised, ComplaintPriority.Emergency);

        Assert.True(emergency < high);
        Assert.True(high < normal);
        Assert.True(normal < low);
    }

    [Theory]
    [InlineData(ComplaintCategory.Lift)]
    [InlineData(ComplaintCategory.Electrical)]
    [InlineData(ComplaintCategory.Security)]
    public void Urgent_categories_are_escalated_above_what_the_resident_chose(ComplaintCategory category)
    {
        // A resident reporting a stuck lift as "Normal" is describing an emergency and does
        // not know the platform's vocabulary.
        var effective = SlaPolicy.EffectivePriority(category, ComplaintPriority.Normal);

        Assert.Equal(ComplaintPriority.High, effective);
    }

    [Fact]
    public void An_urgent_category_never_downgrades_a_higher_choice()
    {
        // The escalation is a floor, not an override. A resident who says Emergency means it.
        var effective = SlaPolicy.EffectivePriority(
            ComplaintCategory.Lift, ComplaintPriority.Emergency);

        Assert.Equal(ComplaintPriority.Emergency, effective);
    }

    [Fact]
    public void An_ordinary_category_keeps_the_chosen_priority()
    {
        Assert.Equal(
            ComplaintPriority.Low,
            SlaPolicy.EffectivePriority(ComplaintCategory.Noise, ComplaintPriority.Low));
    }

    [Fact]
    public void The_deadline_is_computed_in_the_societys_timezone_not_the_servers()
    {
        // A server in UTC and a society in India disagree about when the working day starts.
        // Computing in UTC would open the clock at 13:30 IST.
        var raised = IndiaTime(1, 9);

        var inIndia = SlaPolicy.CalculateDueAt(raised, ComplaintPriority.Normal, India);
        var inUtc = SlaPolicy.CalculateDueAt(raised, ComplaintPriority.Normal, TimeZoneInfo.Utc);

        Assert.NotEqual(inIndia, inUtc);
    }

    [Fact]
    public void Remaining_budget_goes_negative_once_breached()
    {
        var due = IndiaTime(2, 12);

        Assert.True(SlaPolicy.RemainingBudget(due, IndiaTime(2, 10)) > TimeSpan.Zero);
        Assert.True(SlaPolicy.RemainingBudget(due, IndiaTime(2, 14)) < TimeSpan.Zero);
    }
}
