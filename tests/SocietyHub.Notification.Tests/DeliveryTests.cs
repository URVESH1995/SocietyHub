using SocietyHub.Notification.Api.Domain;

namespace SocietyHub.Notification.Tests;

/// <summary>Retry, dead-lettering, and the preference rules that Critical overrides.</summary>
public sealed class DeliveryTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);

    private static NotificationDelivery Delivery(
        NotificationUrgency urgency = NotificationUrgency.Normal) =>
        new(Guid.CreateVersion7(),
            SocietyId,
            UserId,
            "ComplaintRaised",
            NotificationChannel.Push,
            urgency,
            "en-IN",
            "Complaint registered",
            "CMP-2026-00412 registered.",
            Now);

    [Fact]
    public void A_new_delivery_is_pending_and_due_immediately()
    {
        var delivery = Delivery();

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(Now, delivery.NextAttemptAtUtc);
        Assert.False(delivery.IsTerminal);
    }

    [Fact]
    public void A_failure_backs_off_exponentially()
    {
        var delivery = Delivery();

        delivery.RecordFailure("provider timeout", Now, Backoff);
        Assert.Equal(Now.AddSeconds(30), delivery.NextAttemptAtUtc);

        delivery.RecordFailure("provider timeout", Now, Backoff);
        Assert.Equal(Now.AddSeconds(60), delivery.NextAttemptAtUtc);

        delivery.RecordFailure("provider timeout", Now, Backoff);
        Assert.Equal(Now.AddSeconds(120), delivery.NextAttemptAtUtc);
    }

    [Fact]
    public void A_delivery_dead_letters_after_its_attempt_limit()
    {
        var delivery = Delivery();

        for (var i = 0; i < NotificationDelivery.MaxAttempts - 1; i++)
        {
            Assert.True(delivery.RecordFailure("down", Now, Backoff).IsSuccess);
        }

        var final = delivery.RecordFailure("down", Now, Backoff);

        Assert.True(final.IsFailure);
        Assert.Equal(DeliveryStatus.DeadLettered, delivery.Status);
        Assert.True(delivery.IsTerminal);
    }

    [Fact]
    public void A_dead_lettered_delivery_is_kept_with_its_reason()
    {
        // Never deleted. An undelivered message is exactly the one an operator needs to see,
        // and for a Critical one, to act on by hand.
        var delivery = Delivery(NotificationUrgency.Critical);

        for (var i = 0; i < NotificationDelivery.MaxAttempts; i++)
        {
            delivery.RecordFailure("carrier rejected the number", Now, Backoff);
        }

        Assert.Equal(DeliveryStatus.DeadLettered, delivery.Status);
        Assert.Contains("carrier rejected", delivery.LastError);
        Assert.Equal(NotificationDelivery.MaxAttempts, delivery.AttemptCount);
    }

    [Fact]
    public void A_long_error_is_truncated_rather_than_overflowing_the_column()
    {
        var delivery = Delivery();

        delivery.RecordFailure(new string('x', 5000), Now, Backoff);

        Assert.NotNull(delivery.LastError);
        Assert.Equal(1000, delivery.LastError!.Length);
    }

    [Fact]
    public void Sending_clears_the_previous_error()
    {
        var delivery = Delivery();
        delivery.RecordFailure("transient", Now, Backoff);

        delivery.MarkSent(Now.AddMinutes(1), "provider-123");

        Assert.Equal(DeliveryStatus.Sent, delivery.Status);
        Assert.Null(delivery.LastError);
        Assert.Equal("provider-123", delivery.ProviderMessageId);
    }

    [Fact]
    public void A_deferred_delivery_returns_to_pending_when_released()
    {
        // Deferred, not dropped: a notice sent at 11pm is still worth reading at 8am.
        var delivery = Delivery();
        var release = Now.AddHours(8);

        delivery.Defer(release);
        Assert.Equal(DeliveryStatus.Deferred, delivery.Status);
        Assert.Equal(release, delivery.NextAttemptAtUtc);

        delivery.Release();
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
    }

    [Fact]
    public void A_suppressed_delivery_is_terminal_and_records_why()
    {
        var delivery = Delivery();

        delivery.MarkSuppressed("Recipient disabled the Push channel.");

        Assert.Equal(DeliveryStatus.Suppressed, delivery.Status);
        Assert.True(delivery.IsTerminal);
        Assert.Contains("disabled", delivery.LastError);
    }
}

/// <summary>Preferences are real, with one deliberate limit.</summary>
public sealed class PreferenceTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static NotificationPreference Preference() =>
        new(Guid.CreateVersion7(), SocietyId, UserId);

    [Fact]
    public void Defaults_reach_a_new_resident_without_any_setup()
    {
        // Opt-in-to-everything would mean nobody hears about their first visitor.
        var preference = Preference();

        Assert.True(preference.AllowsChannel(NotificationChannel.Push));
        Assert.True(preference.AllowsChannel(NotificationChannel.Sms));
        Assert.True(preference.AllowsChannel(NotificationChannel.InApp));

        // WhatsApp costs the most and needs explicit consent under India's messaging rules.
        Assert.False(preference.AllowsChannel(NotificationChannel.WhatsApp));
    }

    [Fact]
    public void Disabling_a_channel_is_respected()
    {
        var preference = Preference();
        preference.PushEnabled = false;

        Assert.False(preference.AllowsChannel(NotificationChannel.Push));
    }

    [Fact]
    public void In_app_cannot_be_switched_off()
    {
        // It is the record a resident checks later. Letting someone opt out of it would
        // remove the evidence along with the noise.
        var preference = Preference();
        preference.PushEnabled = false;
        preference.SmsEnabled = false;
        preference.EmailEnabled = false;

        Assert.True(preference.AllowsChannel(NotificationChannel.InApp));
    }

    [Fact]
    public void Muting_matches_the_event_key_case_insensitively()
    {
        var preference = Preference();
        preference.MutedEventKeys = "AttendancePunched, DriveOpened";

        Assert.True(preference.IsMuted("AttendancePunched"));
        Assert.True(preference.IsMuted("driveopened"));
        Assert.False(preference.IsMuted("SosRaised"));
    }

    [Fact]
    public void No_muted_list_mutes_nothing()
    {
        Assert.False(Preference().IsMuted("AnythingAtAll"));
    }
}
