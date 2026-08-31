using Microsoft.Extensions.Logging;
using SocietyHub.Notification.Api.Domain;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Notification.Api.Channels;

public sealed record SendOutcome(bool Delivered, string? ProviderMessageId, string? Error);

/// <summary>
/// One way of reaching a person.
///
/// Providers are swapped per environment and per market — MSG91 or Gupshup for Indian SMS,
/// Firebase for push, a different aggregator entirely if the platform leaves India. Keeping
/// them behind this interface means the routing rules, quiet hours and retry logic are written
/// once and none of them know which vendor is on the other end.
/// </summary>
public interface INotificationChannelProvider
{
    NotificationChannel Channel { get; }

    /// <summary>
    /// Attempts one send.
    ///
    /// Returns an outcome rather than throwing on a provider rejection, because "this number
    /// is unreachable" is an expected result that belongs in the delivery log, not an
    /// exception. Genuine faults — a malformed request, a broken credential — still throw.
    /// </summary>
    Task<SendOutcome> SendAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes the message to the resident's in-app inbox.
///
/// Always available and always used. It is the record a resident can scroll back through, and
/// the thing that makes "I was never told" answerable — which is why it has no external
/// dependency that could fail.
/// </summary>
public sealed class InAppChannelProvider : INotificationChannelProvider
{
    public NotificationChannel Channel => NotificationChannel.InApp;

    public Task<SendOutcome> SendAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default) =>
        // The delivery row itself is the inbox entry, so marking it sent is the whole
        // operation. No provider, nothing to fail.
        Task.FromResult(new SendOutcome(true, null, null));
}

/// <summary>
/// Stands in for Firebase Cloud Messaging until credentials exist.
///
/// Logs what it would have sent rather than pretending to succeed silently, so a developer can
/// see the message and its channel in the console. Swapping in the real provider is one
/// registration change.
/// </summary>
public sealed class LoggingPushProvider : INotificationChannelProvider
{
    private readonly ILogger<LoggingPushProvider> _logger;

    public LoggingPushProvider(ILogger<LoggingPushProvider> logger) => _logger = logger;

    public NotificationChannel Channel => NotificationChannel.Push;

    public Task<SendOutcome> SendAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delivery.Destination))
        {
            // Not a fault. A resident who has never opened the app has no push token, and
            // retrying will not produce one — so this fails permanently rather than looping.
            return Task.FromResult(
                new SendOutcome(false, null, "No push token registered for this user."));
        }

        _logger.LogInformation(
            "[PUSH] to {Destination} ({Urgency}): {Subject} — {Body}",
            delivery.Destination,
            delivery.Urgency,
            delivery.Subject,
            delivery.Body);

        return Task.FromResult(new SendOutcome(true, $"dev-push-{delivery.Id:N}", null));
    }
}

/// <summary>
/// Stands in for an Indian SMS aggregator.
///
/// Logs the cost of every message it would have sent, because SMS volume is the line item
/// most likely to be discovered too late. Seeing it in development is the cheapest possible
/// version of that discovery.
/// </summary>
public sealed class LoggingSmsProvider : INotificationChannelProvider
{
    private const decimal RupeesPerMessage = 0.13m;

    private readonly ILogger<LoggingSmsProvider> _logger;

    public LoggingSmsProvider(ILogger<LoggingSmsProvider> logger) => _logger = logger;

    public NotificationChannel Channel => NotificationChannel.Sms;

    public Task<SendOutcome> SendAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delivery.Destination))
        {
            return Task.FromResult(new SendOutcome(false, null, "No phone number on file."));
        }

        _logger.LogWarning(
            "[SMS ₹{Cost}] to {Destination} ({Urgency}): {Body}",
            RupeesPerMessage,
            delivery.Destination,
            delivery.Urgency,
            delivery.Body);

        return Task.FromResult(new SendOutcome(true, $"dev-sms-{delivery.Id:N}", null));
    }
}

public sealed class LoggingEmailProvider : INotificationChannelProvider
{
    private readonly ILogger<LoggingEmailProvider> _logger;

    public LoggingEmailProvider(ILogger<LoggingEmailProvider> logger) => _logger = logger;

    public NotificationChannel Channel => NotificationChannel.Email;

    public Task<SendOutcome> SendAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(delivery.Destination))
        {
            return Task.FromResult(new SendOutcome(false, null, "No email address on file."));
        }

        _logger.LogInformation(
            "[EMAIL] to {Destination}: {Subject}", delivery.Destination, delivery.Subject);

        return Task.FromResult(new SendOutcome(true, $"dev-email-{delivery.Id:N}", null));
    }
}

/// <summary>
/// Resolves the provider for a channel.
///
/// A missing provider is a configuration error, not a delivery failure — silently dropping
/// every message on an unregistered channel is the kind of fault that goes unnoticed for
/// weeks, so it throws.
/// </summary>
public sealed class ChannelProviderRegistry
{
    private readonly Dictionary<NotificationChannel, INotificationChannelProvider> _providers;

    public ChannelProviderRegistry(IEnumerable<INotificationChannelProvider> providers) =>
        _providers = providers.ToDictionary(p => p.Channel);

    public INotificationChannelProvider For(NotificationChannel channel) =>
        _providers.TryGetValue(channel, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"No provider is registered for the {channel} channel.");

    public bool Supports(NotificationChannel channel) => _providers.ContainsKey(channel);
}
