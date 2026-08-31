using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Notification.Api.Domain;
using SocietyHub.Notification.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.Web.Security;

namespace SocietyHub.Notification.Api.Features;

public sealed record UpdatePreferencesRequest(
    bool PushEnabled,
    bool SmsEnabled,
    bool EmailEnabled,
    bool WhatsAppEnabled,
    IReadOnlyList<string>? MutedEventKeys,
    TimeOnly? QuietHoursStart,
    TimeOnly? QuietHoursEnd);

public sealed record RegisterPushTokenRequest(string Token);

/// <summary>
/// A resident's notification settings and their in-app inbox.
///
/// The opt-out is real but bounded: Critical alerts ignore every preference, because a
/// setting that could silence a fire alarm is a setting that will eventually kill somebody.
/// The UI says so rather than hiding it.
/// </summary>
public static class PreferenceEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications");

        group.MapGet("/preferences", GetAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Returns the caller's notification settings.");

        group.MapPut("/preferences", UpdateAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Updates channels, muted events and quiet hours.");

        group.MapPost("/push-token", RegisterTokenAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Registers this device for push. Without one, push silently reaches nobody.");

        group.MapGet("/inbox", InboxAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("The caller's in-app notification history.");

        return app;
    }

    private static async Task<IResult> GetAsync(
        NotificationDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        var preference = await context.Preferences
            .SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        // No row means defaults, which is what a new resident has. Returning the defaults
        // rather than 404 keeps the client from special-casing a state that is normal.
        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            pushEnabled = preference?.PushEnabled ?? true,
            smsEnabled = preference?.SmsEnabled ?? true,
            emailEnabled = preference?.EmailEnabled ?? true,
            whatsAppEnabled = preference?.WhatsAppEnabled ?? false,
            mutedEventKeys = preference?.MutedEventKeys?.Split(',') ?? [],
            quietHoursStart = preference?.QuietHoursStart ?? DeliveryPolicy.DefaultQuietStart,
            quietHoursEnd = preference?.QuietHoursEnd ?? DeliveryPolicy.DefaultQuietEnd,
            hasPushToken = !string.IsNullOrWhiteSpace(preference?.PushToken),
            note = "Emergency alerts ignore these settings.",
        });
    }

    private static async Task<IResult> UpdateAsync(
        UpdatePreferencesRequest request,
        NotificationDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var preference = await GetOrCreateAsync(context, tenant, currentUser, cancellationToken);

        preference.PushEnabled = request.PushEnabled;
        preference.SmsEnabled = request.SmsEnabled;
        preference.EmailEnabled = request.EmailEnabled;
        preference.WhatsAppEnabled = request.WhatsAppEnabled;
        preference.QuietHoursStart = request.QuietHoursStart;
        preference.QuietHoursEnd = request.QuietHoursEnd;

        preference.MutedEventKeys = request.MutedEventKeys is { Count: > 0 }
            ? string.Join(',', request.MutedEventKeys)
            : null;

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> RegisterTokenAsync(
        RegisterPushTokenRequest request,
        NotificationDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var preference = await GetOrCreateAsync(context, tenant, currentUser, cancellationToken);
        preference.PushToken = request.Token;

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> InboxAsync(
        NotificationDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        var inbox = await context.Deliveries
            .Where(d => d.RecipientUserId == userId && d.Channel == NotificationChannel.InApp)
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(100)
            .Select(d => new
            {
                d.Id,
                d.EventKey,
                d.Subject,
                d.Body,
                d.CreatedAtUtc,
                Status = d.Status.ToString(),
            })
            .ToListAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(inbox);
    }

    private static async Task<NotificationPreference> GetOrCreateAsync(
        NotificationDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        var preference = await context.Preferences
            .SingleOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (preference is null)
        {
            preference = new NotificationPreference(
                Guid.CreateVersion7(), tenant.RequireSocietyId(), userId);

            context.Preferences.Add(preference);
        }

        return preference;
    }
}
