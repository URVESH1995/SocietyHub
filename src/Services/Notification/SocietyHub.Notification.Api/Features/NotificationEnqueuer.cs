using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Notification.Api.Domain;
using SocietyHub.Notification.Api.Persistence;

namespace SocietyHub.Notification.Api.Features;

/// <summary>One person to notify, and what we know about how to reach them.</summary>
public sealed record Recipient(
    Guid UserId,
    string Language,
    string? PhoneNumber,
    string? Email);

public interface INotificationEnqueuer
{
    /// <summary>
    /// Fans one event out to its recipients, on the channels the policy and their preferences
    /// allow. Stages rows only — the dispatcher sends them.
    /// </summary>
    Task<int> EnqueueAsync(
        Guid societyId,
        string eventKey,
        IReadOnlyCollection<Recipient> recipients,
        IReadOnlyDictionary<string, string?> values,
        Guid? sourceEventId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns one event into delivery rows.
///
/// Rendering happens here, at enqueue, rather than at send. It costs a little storage per row
/// and buys two things: a message says what it said when the event happened even if the
/// template is edited afterwards, and the dispatcher does no template lookups on its hot path.
/// </summary>
public sealed class NotificationEnqueuer : INotificationEnqueuer
{
    private readonly NotificationDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationEnqueuer> _logger;

    public NotificationEnqueuer(
        NotificationDbContext context,
        TimeProvider timeProvider,
        ILogger<NotificationEnqueuer> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> EnqueueAsync(
        Guid societyId,
        string eventKey,
        IReadOnlyCollection<Recipient> recipients,
        IReadOnlyDictionary<string, string?> values,
        Guid? sourceEventId,
        CancellationToken cancellationToken = default)
    {
        if (recipients.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var urgency = DeliveryPolicy.UrgencyFor(eventKey);
        var channels = DeliveryPolicy.ChannelsFor(urgency);

        // Society timezone would come from the cached society profile; Asia/Kolkata is the
        // only market at launch and the lookup is P1-09's cache, wired when Society exposes it.
        var societyTimeZone = ResolveTimeZone("Asia/Kolkata");

        var userIds = recipients.Select(r => r.UserId).ToList();

        // One query for every recipient's preferences rather than one per person. A notice to
        // a 250-flat society would otherwise issue 600 round trips inside one handler.
        var preferences = await _context.Preferences
            .Where(p => userIds.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, cancellationToken);

        var languages = recipients.Select(r => r.Language).Distinct().ToList();

        var templates = await _context.Templates
            .Where(t => t.EventKey == eventKey && languages.Contains(t.Language))
            .ToListAsync(cancellationToken);

        var staged = 0;

        foreach (var recipient in recipients)
        {
            preferences.TryGetValue(recipient.UserId, out var preference);

            // A muted event is dropped entirely rather than written as suppressed. The
            // resident asked not to hear about attendance punches; recording 400 suppressed
            // rows a month for them would bury the log they actually want to read.
            if (preference?.IsMuted(eventKey) == true && urgency != NotificationUrgency.Critical)
            {
                continue;
            }

            foreach (var channel in channels)
            {
                var template = FindTemplate(templates, recipient.Language, channel);

                if (template is null)
                {
                    // A missing template is a content gap, and silence is the worst response
                    // to it — the resident hears nothing and nobody finds out for weeks.
                    _logger.LogError(
                        "No {Channel} template for {EventKey} in {Language}; nothing sent.",
                        channel,
                        eventKey,
                        recipient.Language);
                    continue;
                }

                var delivery = new NotificationDelivery(
                    Guid.CreateVersion7(),
                    societyId,
                    recipient.UserId,
                    eventKey,
                    channel,
                    urgency,
                    recipient.Language,
                    template.RenderSubject(values),
                    template.Render(values),
                    now)
                {
                    SourceEventId = sourceEventId,
                    Destination = DestinationFor(channel, recipient, preference),
                };

                // Critical overrides every preference. Someone who muted push notifications
                // still needs to hear that the building is on fire.
                if (urgency != NotificationUrgency.Critical
                    && preference?.AllowsChannel(channel) == false)
                {
                    delivery.MarkSuppressed($"Recipient disabled the {channel} channel.");
                }
                else
                {
                    var deferUntil = DeliveryPolicy.DeferUntil(
                        urgency,
                        now,
                        societyTimeZone,
                        preference?.QuietHoursStart ?? DeliveryPolicy.DefaultQuietStart,
                        preference?.QuietHoursEnd ?? DeliveryPolicy.DefaultQuietEnd);

                    if (deferUntil is { } release)
                    {
                        delivery.Defer(release);
                    }
                }

                _context.Deliveries.Add(delivery);
                staged++;
            }
        }

        return staged;
    }

    /// <summary>
    /// Finds the template, falling back to English when the resident's language has none.
    ///
    /// A message in the wrong language beats no message at all — especially for a visitor at
    /// the gate. The error logged above is what gets the gap fixed.
    /// </summary>
    private static NotificationTemplate? FindTemplate(
        List<NotificationTemplate> templates,
        string language,
        NotificationChannel channel) =>
        templates.FirstOrDefault(t => t.Language == language && t.Channel == channel)
        ?? templates.FirstOrDefault(t => t.Language == "en-IN" && t.Channel == channel);

    private static string? DestinationFor(
        NotificationChannel channel,
        Recipient recipient,
        NotificationPreference? preference) => channel switch
    {
        NotificationChannel.Push => preference?.PushToken,
        NotificationChannel.Sms => recipient.PhoneNumber,
        NotificationChannel.WhatsApp => recipient.PhoneNumber,
        NotificationChannel.Email => recipient.Email,
        _ => null,
    };

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A bad timezone must not stop a notification. Falling back to UTC makes quiet
            // hours wrong for that society; throwing would make them silent entirely.
            return TimeZoneInfo.Utc;
        }
    }
}
