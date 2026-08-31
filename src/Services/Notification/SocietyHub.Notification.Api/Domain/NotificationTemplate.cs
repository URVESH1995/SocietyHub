using System.Text.RegularExpressions;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Notification.Api.Domain;

/// <summary>How a message reaches someone. Ordered by cost, cheapest first.</summary>
public enum NotificationChannel
{
    /// <summary>
    /// Free, and therefore the default for everything. At 300,000 to 1,000,000 messages a day
    /// the difference between push and SMS is the difference between a rounding error and the
    /// largest line on the operating bill.
    /// </summary>
    Push = 0,

    Email = 1,

    /// <summary>Roughly ₹0.13 each. Reserved for OTP and life safety.</summary>
    Sms = 2,

    /// <summary>₹0.35–0.80 for a utility message. Higher engagement, higher cost.</summary>
    WhatsApp = 3,

    /// <summary>Shown in the app. Free and always written, whatever else is sent.</summary>
    InApp = 4,
}

/// <summary>
/// How urgent a notification is, which decides both delivery lane and whether quiet hours
/// apply.
/// </summary>
public enum NotificationUrgency
{
    /// <summary>Bulk drives, digests, marketing. Held until morning.</summary>
    Low = 0,

    /// <summary>Complaints, notices, directory changes. Held during quiet hours.</summary>
    Normal = 1,

    /// <summary>A visitor is at the gate. The resident is waiting; quiet hours do not apply.</summary>
    Timely = 2,

    /// <summary>SOS, fire, a fall. Never suppressed, never delayed, always multi-channel.</summary>
    Critical = 3,
}

/// <summary>
/// One message body, for one event, in one language.
///
/// Templates live in the database rather than in resource files because a committee changing
/// the wording of a visitor-arrival notice should not require a deployment — and because a
/// language ships when its templates are complete, which is a content milestone rather than a
/// code one.
/// </summary>
public sealed partial class NotificationTemplate : Entity
{
    public NotificationTemplate(
        Guid id,
        string eventKey,
        string language,
        NotificationChannel channel,
        string? subject,
        string body) : base(id)
    {
        EventKey = eventKey;
        Language = language;
        Channel = channel;
        Subject = subject;
        Body = body;
    }

    private NotificationTemplate()
    {
    }

    /// <summary>
    /// The integration event this renders, e.g. <c>VisitorCheckedIn</c>. Not the CLR type
    /// name — a template must survive a class being renamed.
    /// </summary>
    public string EventKey { get; private set; } = string.Empty;

    /// <summary>BCP-47 tag. Only languages with a complete set are offered to residents.</summary>
    public string Language { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    /// <summary>Email subject or push title. Null for SMS, which has no subject.</summary>
    public string? Subject { get; private set; }

    /// <summary>Body with <c>{placeholder}</c> tokens.</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// Renders the template against the supplied values.
    ///
    /// An unknown placeholder is left in place rather than blanked. A resident receiving
    /// "Your visitor {visitorName} has arrived" knows something is broken; one receiving
    /// "Your visitor  has arrived" assumes the platform is simply careless — and nobody gets
    /// alerted either way. The literal token is what makes the bug reportable.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string?> values) =>
        PlaceholderRegex().Replace(Body, match =>
        {
            var key = match.Groups[1].Value;
            return values.TryGetValue(key, out var value) && value is not null
                ? value
                : match.Value;
        });

    public string? RenderSubject(IReadOnlyDictionary<string, string?> values) =>
        Subject is null
            ? null
            : PlaceholderRegex().Replace(Subject, match =>
            {
                var key = match.Groups[1].Value;
                return values.TryGetValue(key, out var value) && value is not null
                    ? value
                    : match.Value;
            });

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PlaceholderRegex();
}
