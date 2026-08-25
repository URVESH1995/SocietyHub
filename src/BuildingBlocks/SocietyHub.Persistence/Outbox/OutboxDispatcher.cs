using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SocietyHub.Persistence.Outbox;

/// <summary>
/// Performs a single publish pass over the outbox.
///
/// Separate from <see cref="OutboxProcessor"/> so the interesting behaviour — ordering,
/// backoff, poisoning — can be tested by calling one method, rather than by starting a
/// hosted service and racing its timer.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly DbContext _context;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        DbContext context,
        IIntegrationEventPublisher publisher,
        IOptions<OutboxOptions> options,
        TimeProvider timeProvider,
        ILogger<OutboxDispatcher> logger)
    {
        _context = context;
        _publisher = publisher;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Publishes one batch. Returns how many were published, which the processor uses to
    /// decide whether to keep draining or wait for the next poll.
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var pending = await _context.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null
                        && !m.IsPoisoned
                        && m.NextAttemptAtUtc <= now)
            // Causal order. Two events about the same flat must reach consumers in the order
            // they happened, or a check-out arrives before the check-in it closes.
            .OrderBy(m => m.OccurredAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var succeeded = 0;

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var integrationEvent = IntegrationEventSerializer.Deserialize(
                    message.EventType, message.Payload);

                await _publisher.PublishAsync(integrationEvent, cancellationToken);

                message.ProcessedAtUtc = _timeProvider.GetUtcNow();
                message.LastError = null;
                succeeded++;
            }
            catch (UnknownIntegrationEventException ex)
            {
                // Not transient: the type is absent from this build, so no number of retries
                // will resolve it. Poison immediately rather than burning a slot in every
                // batch until the attempt limit is reached.
                message.AttemptCount++;
                message.IsPoisoned = true;
                message.LastError = Truncate(ex.Message);

                _logger.LogError(
                    ex,
                    "Outbox message {MessageId} references unknown event type {EventType}; poisoned.",
                    message.Id,
                    message.EventType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure(message, ex);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return succeeded;
    }

    private void RecordFailure(OutboxMessage message, Exception ex)
    {
        message.AttemptCount++;
        message.LastError = Truncate(ex.Message);

        if (message.AttemptCount >= _options.MaxAttempts)
        {
            message.IsPoisoned = true;

            _logger.LogError(
                ex,
                "Outbox message {MessageId} ({EventType}) poisoned after {Attempts} attempts.",
                message.Id,
                message.EventType,
                message.AttemptCount);
            return;
        }

        // Exponential backoff, capped. A broker that is down deserves patience rather than a
        // hammering, and the cap stops the delay growing past any useful length.
        var delay = TimeSpan.FromTicks(
            Math.Min(
                _options.BaseBackoff.Ticks * (1L << (message.AttemptCount - 1)),
                _options.MaxBackoff.Ticks));

        message.NextAttemptAtUtc = _timeProvider.GetUtcNow().Add(delay);

        _logger.LogWarning(
            ex,
            "Outbox message {MessageId} failed (attempt {Attempt}); retrying in {Delay}.",
            message.Id,
            message.AttemptCount,
            delay);
    }

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000];
}
