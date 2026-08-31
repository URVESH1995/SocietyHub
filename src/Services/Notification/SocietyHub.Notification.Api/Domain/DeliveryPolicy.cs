namespace SocietyHub.Notification.Api.Domain;

/// <summary>
/// Decides which channels an event uses and whether quiet hours apply.
///
/// This is where the platform's largest operating cost is controlled. At full scale the
/// system produces 300,000 to 1,000,000 notifications a day; routing even a fifth of those
/// over SMS would cost roughly ₹4 lakh a month, which is twice the entire cloud bill. So push
/// carries almost everything, and SMS is spent only where a missed message has a real cost.
/// </summary>
public static class DeliveryPolicy
{
    /// <summary>Society-local default. Overridden per resident.</summary>
    public static readonly TimeOnly DefaultQuietStart = new(22, 0);

    public static readonly TimeOnly DefaultQuietEnd = new(7, 0);

    public static NotificationUrgency UrgencyFor(string eventKey) => eventKey switch
    {
        // Life safety. Never suppressed, never deferred, sent on every channel available.
        "SosRaised" => NotificationUrgency.Critical,
        "FireOrSmokeDetected" => NotificationUrgency.Critical,
        "FallDetected" => NotificationUrgency.Critical,

        // Someone is standing at the gate. A notification that arrives after they have been
        // turned away is worse than useless, so quiet hours do not apply.
        "VisitorCheckedIn" => NotificationUrgency.Timely,
        "VisitorPreApproved" => NotificationUrgency.Timely,
        "TailgatingDetected" => NotificationUrgency.Timely,

        // A missed SLA is urgent to the society but not to a sleeping resident.
        "ComplaintSlaBreached" => NotificationUrgency.Normal,
        "ComplaintRaised" => NotificationUrgency.Normal,
        "ComplaintAssigned" => NotificationUrgency.Normal,
        "ComplaintResolved" => NotificationUrgency.Normal,
        "NoticePublished" => NotificationUrgency.Normal,

        "DriveOpened" => NotificationUrgency.Low,
        "DriveQuorumReached" => NotificationUrgency.Low,

        _ => NotificationUrgency.Normal,
    };

    /// <summary>
    /// Which channels to use, cheapest sufficient set.
    ///
    /// Only Critical earns SMS, and it earns it because a push notification to a phone that is
    /// off, out of battery or without data reaches nobody — and for a fire that is not an
    /// acceptable failure. Everything else is push plus the in-app record.
    /// </summary>
    public static IReadOnlyList<NotificationChannel> ChannelsFor(NotificationUrgency urgency) =>
        urgency switch
        {
            NotificationUrgency.Critical =>
                [NotificationChannel.Push, NotificationChannel.Sms, NotificationChannel.InApp],

            NotificationUrgency.Timely =>
                [NotificationChannel.Push, NotificationChannel.InApp],

            NotificationUrgency.Normal =>
                [NotificationChannel.Push, NotificationChannel.InApp],

            // Deliberately in-app only. A bulk-drive announcement is something a resident
            // looks for, not something that should interrupt them.
            NotificationUrgency.Low => [NotificationChannel.InApp],

            _ => [NotificationChannel.InApp],
        };

    /// <summary>
    /// Whether a message must wait, and until when.
    ///
    /// Evaluated in society-local time. A platform-wide UTC quiet window would silence Indian
    /// residents through the afternoon and wake them at 3:30am.
    /// </summary>
    public static DateTimeOffset? DeferUntil(
        NotificationUrgency urgency,
        DateTimeOffset nowUtc,
        TimeZoneInfo societyTimeZone,
        TimeOnly quietStart,
        TimeOnly quietEnd)
    {
        // Critical and Timely both ignore quiet hours, for different reasons: one is a fire,
        // the other is a person at the door who will not wait until morning.
        if (urgency is NotificationUrgency.Critical or NotificationUrgency.Timely)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, societyTimeZone);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        // Two shapes of window, and they behave differently at both ends.
        //
        // The default wraps midnight (22:00–07:00), so "inside" means after the start OR
        // before the end. A night-shift worker's window does not (08:00–16:00), where inside
        // means between the two.
        var wrapsMidnight = quietStart > quietEnd;

        var isQuiet = wrapsMidnight
            ? localTime >= quietStart || localTime < quietEnd
            : localTime >= quietStart && localTime < quietEnd;

        if (!isQuiet)
        {
            return null;
        }

        // The end of the window is tomorrow only when the window wraps and we are still in
        // its evening half. Everywhere else — the small hours of a wrapping window, or any
        // point inside a same-day window — the end is today. Getting this wrong holds a
        // message an extra 24 hours, which looks exactly like losing it.
        var releaseDate = wrapsMidnight && localTime >= quietStart
            ? local.Date.AddDays(1)
            : local.Date;

        var release = new DateTimeOffset(
            releaseDate.Year, releaseDate.Month, releaseDate.Day,
            quietEnd.Hour, quietEnd.Minute, 0,
            local.Offset);

        return release.ToUniversalTime();
    }
}
