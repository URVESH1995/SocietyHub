using SocietyHub.Notification.Api.Domain;
using SocietyHub.Notification.Api.Persistence;

namespace SocietyHub.Notification.Tests;

/// <summary>
/// Routing decides the platform's largest operating cost and whether a resident is woken at
/// 3am. Both are easy to get wrong in ways that only show up on a bill or in a complaint.
/// </summary>
public sealed class DeliveryPolicyTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static DateTimeOffset IndiaTime(int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, 9, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, India.GetUtcOffset(local)).ToUniversalTime();
    }

    private static (int Day, int Hour) AsIndiaClock(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, India);
        return (local.Day, local.Hour);
    }

    // --- urgency --------------------------------------------------------

    [Theory]
    [InlineData("SosRaised")]
    [InlineData("FireOrSmokeDetected")]
    [InlineData("FallDetected")]
    public void Life_safety_events_are_critical(string eventKey) =>
        Assert.Equal(NotificationUrgency.Critical, DeliveryPolicy.UrgencyFor(eventKey));

    [Fact]
    public void A_visitor_at_the_gate_is_timely_not_merely_normal()
    {
        // A resident is standing behind a door. A notification that arrives after the visitor
        // has been turned away is worse than none.
        Assert.Equal(NotificationUrgency.Timely, DeliveryPolicy.UrgencyFor("VisitorCheckedIn"));
    }

    [Fact]
    public void An_unclassified_event_defaults_to_normal()
    {
        // Forgetting to classify should make an event ordinary, not silently deprioritise it
        // into the lane that is allowed to lag.
        Assert.Equal(NotificationUrgency.Normal, DeliveryPolicy.UrgencyFor("SomethingNew"));
    }

    // --- cost -----------------------------------------------------------

    [Fact]
    public void Only_critical_messages_are_allowed_to_cost_money()
    {
        // The single most important assertion in this service. SMS at ₹0.13 across a million
        // daily notifications is ₹4 lakh a month — twice the entire cloud bill.
        foreach (var urgency in Enum.GetValues<NotificationUrgency>())
        {
            var channels = DeliveryPolicy.ChannelsFor(urgency);
            var costsMoney = channels.Contains(NotificationChannel.Sms)
                             || channels.Contains(NotificationChannel.WhatsApp);

            if (urgency == NotificationUrgency.Critical)
            {
                Assert.True(costsMoney, "Critical must reach a phone that has no data.");
            }
            else
            {
                Assert.False(costsMoney, $"{urgency} must not use a paid channel.");
            }
        }
    }

    [Fact]
    public void Every_urgency_writes_an_in_app_record()
    {
        // The in-app entry is what makes "I was never told" answerable, so nothing skips it.
        foreach (var urgency in Enum.GetValues<NotificationUrgency>())
        {
            Assert.Contains(NotificationChannel.InApp, DeliveryPolicy.ChannelsFor(urgency));
        }
    }

    [Fact]
    public void Low_urgency_does_not_interrupt_anyone()
    {
        // A bulk-drive announcement is something a resident looks for, not something that
        // should buzz their phone.
        Assert.DoesNotContain(
            NotificationChannel.Push, DeliveryPolicy.ChannelsFor(NotificationUrgency.Low));
    }

    // --- quiet hours ----------------------------------------------------

    [Fact]
    public void A_normal_message_at_midnight_is_held_until_morning()
    {
        var deferUntil = DeliveryPolicy.DeferUntil(
            NotificationUrgency.Normal,
            IndiaTime(1, 23, 30),
            India,
            DeliveryPolicy.DefaultQuietStart,
            DeliveryPolicy.DefaultQuietEnd);

        Assert.NotNull(deferUntil);
        Assert.Equal((2, 7), AsIndiaClock(deferUntil.Value));
    }

    [Fact]
    public void A_message_after_midnight_is_released_the_same_morning()
    {
        // The wrap-around case. 02:00 is inside a 22:00–07:00 window, and the release is
        // today at 07:00 — not tomorrow, which would hold it for 29 hours.
        var deferUntil = DeliveryPolicy.DeferUntil(
            NotificationUrgency.Normal,
            IndiaTime(2, 2),
            India,
            DeliveryPolicy.DefaultQuietStart,
            DeliveryPolicy.DefaultQuietEnd);

        Assert.NotNull(deferUntil);
        Assert.Equal((2, 7), AsIndiaClock(deferUntil.Value));
    }

    [Fact]
    public void A_daytime_message_is_not_held()
    {
        Assert.Null(DeliveryPolicy.DeferUntil(
            NotificationUrgency.Normal,
            IndiaTime(1, 14),
            India,
            DeliveryPolicy.DefaultQuietStart,
            DeliveryPolicy.DefaultQuietEnd));
    }

    [Fact]
    public void An_emergency_at_3am_is_never_held()
    {
        Assert.Null(DeliveryPolicy.DeferUntil(
            NotificationUrgency.Critical,
            IndiaTime(2, 3),
            India,
            DeliveryPolicy.DefaultQuietStart,
            DeliveryPolicy.DefaultQuietEnd));
    }

    [Fact]
    public void A_visitor_arriving_late_is_not_held_either()
    {
        // Timely ignores quiet hours for a different reason than Critical: the visitor is at
        // the door now, and holding the message until 7am makes it pointless.
        Assert.Null(DeliveryPolicy.DeferUntil(
            NotificationUrgency.Timely,
            IndiaTime(1, 23),
            India,
            DeliveryPolicy.DefaultQuietStart,
            DeliveryPolicy.DefaultQuietEnd));
    }

    [Fact]
    public void Quiet_hours_are_evaluated_in_the_societys_timezone_not_utc()
    {
        // 18:00 UTC is 23:30 in India — inside quiet hours there, the middle of the working
        // day in UTC. Getting this wrong silences Indian residents all afternoon and wakes
        // them at 3:30am.
        var moment = new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero);

        var inIndia = DeliveryPolicy.DeferUntil(
            NotificationUrgency.Normal, moment, India,
            DeliveryPolicy.DefaultQuietStart, DeliveryPolicy.DefaultQuietEnd);

        var inUtc = DeliveryPolicy.DeferUntil(
            NotificationUrgency.Normal, moment, TimeZoneInfo.Utc,
            DeliveryPolicy.DefaultQuietStart, DeliveryPolicy.DefaultQuietEnd);

        Assert.NotNull(inIndia);
        Assert.Null(inUtc);
    }

    [Fact]
    public void A_resident_can_set_their_own_window()
    {
        // A night-shift worker sleeps during the day.
        var deferUntil = DeliveryPolicy.DeferUntil(
            NotificationUrgency.Normal,
            IndiaTime(1, 10),
            India,
            new TimeOnly(8, 0),
            new TimeOnly(16, 0));

        Assert.NotNull(deferUntil);
        Assert.Equal((1, 16), AsIndiaClock(deferUntil.Value));
    }
}

/// <summary>
/// A language is offered only when its templates are complete. A gap means a resident who
/// chose Hindi silently receives English, or nothing at all.
/// </summary>
public sealed class TemplateCatalogueTests
{
    [Fact]
    public void Every_event_and_channel_exists_in_both_shipped_languages()
    {
        var all = TemplateSeed.All;

        var missing = all
            .Select(t => (t.EventKey, t.Channel))
            .Distinct()
            .SelectMany(pair => new[] { "en-IN", "hi-IN" }
                .Where(lang => !all.Contains((pair.EventKey, pair.Channel, lang)))
                .Select(lang => $"{pair.EventKey}/{pair.Channel}/{lang}"))
            .ToList();

        Assert.True(missing.Count == 0, $"Missing templates: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Only_the_emergency_event_has_an_sms_template()
    {
        // Templates are the second line of defence on cost. Even if routing were changed by
        // mistake, there is nothing to send over SMS for an ordinary event.
        var smsEvents = TemplateSeed.All
            .Where(t => t.Channel == NotificationChannel.Sms)
            .Select(t => t.EventKey)
            .Distinct()
            .ToList();

        Assert.Equal(["SosRaised"], smsEvents);
    }
}
