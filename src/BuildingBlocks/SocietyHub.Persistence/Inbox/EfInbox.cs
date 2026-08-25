using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts;

namespace SocietyHub.Persistence.Inbox;

/// <summary>
/// Decides whether this consumer should handle an event, or has already done so.
/// </summary>
public interface IInbox
{
    /// <summary>
    /// Stages a claim on <paramref name="integrationEvent"/> for <paramref name="consumerName"/>.
    ///
    /// Returns <see langword="false"/> when this consumer has already handled it, in which
    /// case the caller must skip its work entirely — not run it and discard the result.
    ///
    /// The claim is staged, not committed. Saving it in the same transaction as the handler's
    /// own state change is what makes the pair atomic: if the handler fails, the claim rolls
    /// back with it and the message is genuinely retried rather than silently swallowed.
    /// </summary>
    Task<bool> TryClaimAsync(
        IntegrationEvent integrationEvent,
        string consumerName,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInbox" />
public sealed class EfInbox : IInbox
{
    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public EfInbox(DbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<bool> TryClaimAsync(
        IntegrationEvent integrationEvent,
        string consumerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        // A cheap read that catches the overwhelmingly common case — a redelivery arriving
        // long after the original was handled. It is deliberately not the safety mechanism:
        // two replicas can both read "absent" and both proceed. The primary key is what
        // actually prevents double handling, by making the second INSERT fail on commit.
        var alreadyHandled = await _context.Set<InboxMessage>()
            .AnyAsync(
                m => m.EventId == integrationEvent.EventId && m.ConsumerName == consumerName,
                cancellationToken);

        if (alreadyHandled)
        {
            return false;
        }

        _context.Set<InboxMessage>().Add(new InboxMessage
        {
            EventId = integrationEvent.EventId,
            ConsumerName = consumerName,
            ReceivedAtUtc = _timeProvider.GetUtcNow(),
            SocietyId = integrationEvent.SocietyId,
        });

        return true;
    }
}

/// <summary>
/// Raised when the inbox claim collides on commit, meaning another replica handled the same
/// event concurrently.
///
/// Not an error worth alerting on: it is the deduplication working. Consumers catch it,
/// discard their work and acknowledge the message, because the other replica already did it.
/// </summary>
public sealed class DuplicateInboxClaimException : Exception
{
    public DuplicateInboxClaimException(Guid eventId, string consumerName, Exception inner)
        : base($"Consumer '{consumerName}' lost the race to handle event '{eventId}'; " +
               "another instance claimed it first.", inner)
    {
        EventId = eventId;
        ConsumerName = consumerName;
    }

    public Guid EventId { get; }

    public string ConsumerName { get; }
}
