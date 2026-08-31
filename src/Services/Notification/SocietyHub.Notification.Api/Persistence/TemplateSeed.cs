using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Notification.Api.Domain;

namespace SocietyHub.Notification.Api.Persistence;

/// <summary>
/// The shipped message catalogue, in English and Hindi.
///
/// Both languages are complete on purpose. A language is offered to residents only when every
/// template exists in it — half-translated is worse than absent, because a resident who picks
/// Hindi and then receives English has been misled about what the platform supports.
///
/// Seeded rather than hard-coded so a committee can reword a notice without a deployment.
/// Existing rows are never overwritten: an edit made in production must survive a restart.
/// </summary>
public static class TemplateSeed
{
    private sealed record Entry(
        string EventKey,
        NotificationChannel Channel,
        string Language,
        string? Subject,
        string Body);

    private static readonly Entry[] Templates =
    [
        // --- Visitor arrived -------------------------------------------------
        new("VisitorCheckedIn", NotificationChannel.Push, "en-IN",
            "Visitor at the gate",
            "{visitorName} has arrived at the gate."),

        new("VisitorCheckedIn", NotificationChannel.Push, "hi-IN",
            "गेट पर आगंतुक",
            "{visitorName} गेट पर पहुँच गए हैं।"),

        new("VisitorCheckedIn", NotificationChannel.InApp, "en-IN",
            "Visitor at the gate",
            "{visitorName} ({visitorType}) was let in at the gate."),

        new("VisitorCheckedIn", NotificationChannel.InApp, "hi-IN",
            "गेट पर आगंतुक",
            "{visitorName} ({visitorType}) को गेट से अंदर भेजा गया।"),

        // --- Pass created ----------------------------------------------------
        new("VisitorPreApproved", NotificationChannel.Push, "en-IN",
            "Gate pass created",
            "Pass for {visitorName} is valid until {validUntil}."),

        new("VisitorPreApproved", NotificationChannel.Push, "hi-IN",
            "गेट पास बनाया गया",
            "{visitorName} का पास {validUntil} तक मान्य है।"),

        new("VisitorPreApproved", NotificationChannel.InApp, "en-IN",
            "Gate pass created",
            "You pre-approved {visitorName}. The pass is valid until {validUntil}."),

        new("VisitorPreApproved", NotificationChannel.InApp, "hi-IN",
            "गेट पास बनाया गया",
            "आपने {visitorName} को अनुमति दी। पास {validUntil} तक मान्य है।"),

        // --- SOS -------------------------------------------------------------
        // The only event that gets an SMS template, because a push to a phone that is off
        // reaches nobody and that is not an acceptable failure for an emergency.
        new("SosRaised", NotificationChannel.Push, "en-IN",
            "EMERGENCY",
            "{category} emergency reported at {raisedAt}. Help if you can."),

        new("SosRaised", NotificationChannel.Push, "hi-IN",
            "आपातकाल",
            "{raisedAt} बजे {category} आपातकाल की सूचना। यदि संभव हो तो सहायता करें।"),

        new("SosRaised", NotificationChannel.Sms, "en-IN", null,
            "SOCIETYHUB EMERGENCY: {category} reported at {raisedAt}. Help if you can."),

        new("SosRaised", NotificationChannel.Sms, "hi-IN", null,
            "SOCIETYHUB आपातकाल: {raisedAt} बजे {category} की सूचना। सहायता करें।"),

        new("SosRaised", NotificationChannel.InApp, "en-IN",
            "Emergency alert",
            "A {category} emergency was reported at {raisedAt}."),

        new("SosRaised", NotificationChannel.InApp, "hi-IN",
            "आपातकालीन चेतावनी",
            "{raisedAt} बजे {category} आपातकाल की सूचना दी गई।"),

        // --- Complaints ------------------------------------------------------
        new("ComplaintRaised", NotificationChannel.Push, "en-IN",
            "Complaint registered",
            "{ticketNumber} registered. We aim to resolve it by {slaDue}."),

        new("ComplaintRaised", NotificationChannel.Push, "hi-IN",
            "शिकायत दर्ज",
            "{ticketNumber} दर्ज हुई। हम {slaDue} तक समाधान का प्रयास करेंगे।"),

        new("ComplaintRaised", NotificationChannel.InApp, "en-IN",
            "Complaint registered",
            "{ticketNumber}: {title}. Target resolution {slaDue}."),

        new("ComplaintRaised", NotificationChannel.InApp, "hi-IN",
            "शिकायत दर्ज",
            "{ticketNumber}: {title}. अपेक्षित समाधान {slaDue}।"),

        new("ComplaintResolved", NotificationChannel.Push, "en-IN",
            "Complaint resolved",
            "{ticketNumber} has been resolved. Please confirm."),

        new("ComplaintResolved", NotificationChannel.Push, "hi-IN",
            "शिकायत हल हुई",
            "{ticketNumber} का समाधान हो गया। कृपया पुष्टि करें।"),

        new("ComplaintResolved", NotificationChannel.InApp, "en-IN",
            "Complaint resolved",
            "{ticketNumber} was marked resolved. Confirm it, or reopen if it is not fixed."),

        new("ComplaintResolved", NotificationChannel.InApp, "hi-IN",
            "शिकायत हल हुई",
            "{ticketNumber} हल के रूप में चिह्नित। पुष्टि करें, या ठीक न हो तो पुनः खोलें।"),

        new("ComplaintSlaBreached", NotificationChannel.Push, "en-IN",
            "Complaint overdue",
            "{ticketNumber} has missed its resolution deadline."),

        new("ComplaintSlaBreached", NotificationChannel.Push, "hi-IN",
            "शिकायत विलंबित",
            "{ticketNumber} की समय-सीमा बीत चुकी है।"),

        new("ComplaintSlaBreached", NotificationChannel.InApp, "en-IN",
            "Complaint overdue",
            "{ticketNumber} is overdue and has been escalated to the {audience}."),

        new("ComplaintSlaBreached", NotificationChannel.InApp, "hi-IN",
            "शिकायत विलंबित",
            "{ticketNumber} विलंबित है और {audience} तक भेजी गई है।"),

        // --- Notices ---------------------------------------------------------
        new("NoticePublished", NotificationChannel.Push, "en-IN",
            "{title}",
            "{summary}"),

        new("NoticePublished", NotificationChannel.Push, "hi-IN",
            "{title}",
            "{summary}"),

        new("NoticePublished", NotificationChannel.InApp, "en-IN",
            "{title}",
            "{body}"),

        new("NoticePublished", NotificationChannel.InApp, "hi-IN",
            "{title}",
            "{body}"),
    ];

    public static async Task SeedAsync(
        NotificationDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var existing = await context.Templates
            .Select(t => new { t.EventKey, t.Language, t.Channel })
            .ToListAsync(cancellationToken);

        var present = existing
            .Select(e => (e.EventKey, e.Language, e.Channel))
            .ToHashSet();

        var missing = Templates
            .Where(t => !present.Contains((t.EventKey, t.Language, t.Channel)))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        foreach (var entry in missing)
        {
            context.Templates.Add(new NotificationTemplate(
                Guid.CreateVersion7(),
                entry.EventKey,
                entry.Language,
                entry.Channel,
                entry.Subject,
                entry.Body));
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Count} notification templates.", missing.Count);
    }

    /// <summary>
    /// Every event and channel the catalogue covers, so a test can assert both languages are
    /// complete. A gap here is a resident receiving nothing.
    /// </summary>
    public static IReadOnlyList<(string EventKey, NotificationChannel Channel, string Language)> All =>
        Templates.Select(t => (t.EventKey, t.Channel, t.Language)).ToList();
}
