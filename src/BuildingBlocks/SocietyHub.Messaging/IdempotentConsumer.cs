using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Contracts;
using SocietyHub.Persistence.Inbox;

namespace SocietyHub.Messaging;

/// <summary>
/// Base for consumers that must handle each event exactly once.
///
/// The outbox delivers at-least-once, so duplicates are normal rather than exceptional — a
/// broker redelivery, a processor restart, a replica racing another. Handling one twice means
/// a second SMS at midnight or, in Phase 2, a second charge.
///
/// The claim and the handler's own writes are saved together in one <c>SaveChanges</c>. That
/// is what makes it exactly-once rather than merely deduplicated: if the handler throws, the
/// claim rolls back with it and the redelivery genuinely retries instead of being marked done
/// and silently skipped.
/// </summary>
/// <typeparam name="TEvent">The integration event handled.</typeparam>
public abstract class IdempotentConsumer<TEvent> : IConsumer<TEvent>
    where TEvent : IntegrationEvent
{
    private readonly IInbox _inbox;
    private readonly DbContext _context;
    private readonly ILogger _logger;

    protected IdempotentConsumer(IInbox inbox, DbContext context, ILogger logger)
    {
        _inbox = inbox;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Stable name for this consumer, used as half the deduplication key.
    ///
    /// Must not change when the class is renamed. Changing it makes the consumer reprocess
    /// every event still inside the inbox retention window.
    /// </summary>
    protected abstract string ConsumerName { get; }

    public async Task Consume(ConsumeContext<TEvent> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        var claimed = await _inbox.TryClaimAsync(message, ConsumerName, cancellationToken);

        if (!claimed)
        {
            _logger.LogDebug(
                "{Consumer} already handled event {EventId}; skipping duplicate.",
                ConsumerName,
                message.EventId);
            return;
        }

        try
        {
            await HandleAsync(message, context, cancellationToken);

            // The single commit that makes the claim and the work atomic.
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateClaim(ex))
        {
            // Another replica claimed the same event between our check and our commit. Not an
            // error: deduplication working under a race. The other instance completed the work,
            // so acknowledge and move on rather than retrying into a permanent conflict.
            _logger.LogDebug(
                "{Consumer} lost the race for event {EventId}; another instance handled it.",
                ConsumerName,
                message.EventId);
        }
    }

    /// <summary>
    /// The actual work. Stage writes on the context but do not save — the base class commits
    /// them together with the inbox claim.
    /// </summary>
    protected abstract Task HandleAsync(
        TEvent message,
        ConsumeContext<TEvent> context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Detects a primary key collision on the inbox table.
    ///
    /// Matched on SQL Server's 2601/2627 rather than on message text, which changes with
    /// locale and version. SQLite reports 19 for a constraint violation.
    /// </summary>
    private static bool IsDuplicateClaim(DbUpdateException ex) =>
        ex.InnerException is not null
        && ex.InnerException.GetType().Name switch
        {
            "SqlException" => ExtractNumber(ex.InnerException) is 2601 or 2627,
            "SqliteException" => ExtractNumber(ex.InnerException) is 19,
            _ => false,
        };

    private static int ExtractNumber(Exception exception) =>
        exception.GetType().GetProperty("Number")?.GetValue(exception) as int?
        ?? exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception) as int?
        ?? 0;
}
