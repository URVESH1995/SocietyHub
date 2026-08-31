using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Notification.Api.Domain;

public enum DeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,

    /// <summary>Held by quiet hours; will be released in the morning.</summary>
    Deferred = 3,

    /// <summary>The recipient opted out of this category on this channel.</summary>
    Suppressed = 4,

    /// <summary>Out of attempts. Kept, never deleted — an operator has to be able to see it.</summary>
    DeadLettered = 5,
}

/// <summary>
/// One attempt to reach one person on one channel.
///
/// Written for every send, including the suppressed and deferred ones. That completeness is
/// the point: when a resident says "I never got told my guest arrived", the answer has to be
/// checkable, and "we have no record" is indistinguishable from "we never tried".
/// </summary>
public sealed class NotificationDelivery : Entity, ITenantScoped
{
    /// <summary>Four attempts over roughly fifteen minutes, then dead-letter.</summary>
    public const int MaxAttempts = 4;

    public NotificationDelivery(
        Guid id,
        Guid societyId,
        Guid recipientUserId,
        string eventKey,
        NotificationChannel channel,
        NotificationUrgency urgency,
        string language,
        string? subject,
        string body,
        DateTimeOffset createdAtUtc) : base(id)
    {
        SocietyId = societyId;
        RecipientUserId = recipientUserId;
        EventKey = eventKey;
        Channel = channel;
        Urgency = urgency;
        Language = language;
        Subject = subject;
        Body = body;
        CreatedAtUtc = createdAtUtc;
        NextAttemptAtUtc = createdAtUtc;
        Status = DeliveryStatus.Pending;
    }

    private NotificationDelivery()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid RecipientUserId { get; private set; }

    /// <summary>Ties the delivery back to the event that caused it, for tracing.</summary>
    public Guid? SourceEventId { get; set; }

    public string EventKey { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    public NotificationUrgency Urgency { get; private set; }

    public string Language { get; private set; } = string.Empty;

    public string? Subject { get; private set; }

    /// <summary>Rendered at enqueue, not at send. See the note on <see cref="Render"/>.</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>Push token, phone number or email, resolved at enqueue.</summary>
    public string? Destination { get; set; }

    public DeliveryStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Provider's own identifier, for reconciling against their delivery reports.</summary>
    public string? ProviderMessageId { get; set; }

    public bool IsTerminal =>
        Status is DeliveryStatus.Sent or DeliveryStatus.Suppressed or DeliveryStatus.DeadLettered;

    public void MarkSent(DateTimeOffset now, string? providerMessageId)
    {
        Status = DeliveryStatus.Sent;
        SentAtUtc = now;
        AttemptCount++;
        ProviderMessageId = providerMessageId;
        LastError = null;
    }

    public void MarkSuppressed(string reason)
    {
        Status = DeliveryStatus.Suppressed;
        LastError = reason;
    }

    /// <summary>
    /// Holds the message until quiet hours end.
    ///
    /// Deferred rather than dropped: a notice sent at 11pm is still worth reading at 8am, and
    /// silently discarding it would make the platform look broken to the committee who sent it.
    /// </summary>
    public void Defer(DateTimeOffset releaseAtUtc)
    {
        Status = DeliveryStatus.Deferred;
        NextAttemptAtUtc = releaseAtUtc;
    }

    public void Release() => Status = DeliveryStatus.Pending;

    public Result RecordFailure(string error, DateTimeOffset now, TimeSpan baseBackoff)
    {
        AttemptCount++;
        LastError = error.Length > 1000 ? error[..1000] : error;

        if (AttemptCount >= MaxAttempts)
        {
            // Kept, not deleted. A message nobody could deliver is exactly the one an
            // operator needs to see — and for a Critical one, to act on manually.
            Status = DeliveryStatus.DeadLettered;
            return Error.Failure("Delivery.DeadLettered", "Delivery exhausted its attempts.");
        }

        Status = DeliveryStatus.Pending;
        NextAttemptAtUtc = now.Add(
            TimeSpan.FromTicks(baseBackoff.Ticks * (1L << (AttemptCount - 1))));

        return Result.Success();
    }
}

/// <summary>
/// What one person wants to be told about, and how.
///
/// Absence of a row means defaults apply, so a new resident is reachable without any setup —
/// the alternative, opt-in-to-everything, means nobody hears about their first visitor.
/// </summary>
public sealed class NotificationPreference : Entity, ITenantScoped
{
    public NotificationPreference(Guid id, Guid societyId, Guid userId) : base(id)
    {
        SocietyId = societyId;
        UserId = userId;
    }

    private NotificationPreference()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid UserId { get; private set; }

    public bool PushEnabled { get; set; } = true;

    public bool SmsEnabled { get; set; } = true;

    public bool EmailEnabled { get; set; } = true;

    public bool WhatsAppEnabled { get; set; }

    /// <summary>Comma-separated event keys the user has muted, e.g. daily-help attendance.</summary>
    public string? MutedEventKeys { get; set; }

    /// <summary>Society-local time when quiet hours begin. Null uses the society default.</summary>
    public TimeOnly? QuietHoursStart { get; set; }

    public TimeOnly? QuietHoursEnd { get; set; }

    public string? PushToken { get; set; }

    public bool IsMuted(string eventKey) =>
        MutedEventKeys is not null
        && MutedEventKeys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .Contains(eventKey, StringComparer.OrdinalIgnoreCase);

    public bool AllowsChannel(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Push => PushEnabled,
        NotificationChannel.Sms => SmsEnabled,
        NotificationChannel.Email => EmailEnabled,
        NotificationChannel.WhatsApp => WhatsAppEnabled,

        // In-app is always written. It is the record the resident can check later, and
        // letting someone opt out of it would remove the evidence along with the noise.
        NotificationChannel.InApp => true,
        _ => true,
    };
}
