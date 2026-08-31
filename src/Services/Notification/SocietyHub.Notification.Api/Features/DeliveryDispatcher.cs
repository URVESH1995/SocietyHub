using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocietyHub.Notification.Api.Channels;
using SocietyHub.Notification.Api.Domain;
using SocietyHub.Notification.Api.Persistence;

namespace SocietyHub.Notification.Api.Features;

public sealed class DispatcherOptions
{
    public const string SectionName = "NotificationDispatcher";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 200;

    /// <summary>First retry after 30s, then 1m, 2m, 4m.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Sends what is due, and releases what quiet hours were holding.
///
/// Split from <see cref="DeliveryDispatcherService"/> so the interesting behaviour — ordering,
/// retry, dead-lettering, quiet-hour release — can be tested by calling one method instead of
/// starting a hosted service and racing its timer.
/// </summary>
public sealed class DeliveryDispatcher
{
    private readonly NotificationDbContext _context;
    private readonly ChannelProviderRegistry _providers;
    private readonly DispatcherOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeliveryDispatcher> _logger;

    public DeliveryDispatcher(
        NotificationDbContext context,
        ChannelProviderRegistry providers,
        IOptions<DispatcherOptions> options,
        TimeProvider timeProvider,
        ILogger<DeliveryDispatcher> logger)
    {
        _context = context;
        _providers = providers;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        await ReleaseDeferredAsync(now, cancellationToken);

        // IgnoreQueryFilters because this runs outside any request and legitimately spans
        // societies — which is exactly why it is a background service and not an endpoint.
        var due = await _context.Deliveries
            .IgnoreQueryFilters()
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAtUtc <= now)
            // Critical first, then oldest. A fire alert must not wait behind a backlog of
            // complaint notifications that happened to be enqueued earlier.
            .OrderByDescending(d => d.Urgency)
            .ThenBy(d => d.CreatedAtUtc)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var sent = 0;

        foreach (var delivery in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_providers.Supports(delivery.Channel))
            {
                delivery.RecordFailure(
                    $"No provider registered for {delivery.Channel}.", now, _options.BaseBackoff);
                continue;
            }

            try
            {
                var outcome = await _providers
                    .For(delivery.Channel)
                    .SendAsync(delivery, cancellationToken);

                if (outcome.Delivered)
                {
                    delivery.MarkSent(_timeProvider.GetUtcNow(), outcome.ProviderMessageId);
                    sent++;
                }
                else
                {
                    RecordFailure(delivery, outcome.Error ?? "Provider rejected the message.", now);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure(delivery, ex.Message, now);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return sent;
    }

    /// <summary>
    /// Moves messages whose quiet-hours window has passed back into the queue.
    ///
    /// Done as a set-based update rather than loading and saving each row: at 7am every
    /// deferred message in every society becomes due at once, and that is the one moment this
    /// service is asked to do real work.
    /// </summary>
    private async Task ReleaseDeferredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await _context.Deliveries
            .IgnoreQueryFilters()
            .Where(d => d.Status == DeliveryStatus.Deferred && d.NextAttemptAtUtc <= now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(d => d.Status, DeliveryStatus.Pending),
                cancellationToken);

    private void RecordFailure(NotificationDelivery delivery, string error, DateTimeOffset now)
    {
        var result = delivery.RecordFailure(error, now, _options.BaseBackoff);

        if (result.IsFailure)
        {
            // Dead-lettered. Logged at error for anything, and worth waking someone for when
            // it is Critical — an undelivered fire alert is an incident in itself.
            _logger.Log(
                delivery.Urgency == NotificationUrgency.Critical
                    ? LogLevel.Critical
                    : LogLevel.Error,
                "Delivery {DeliveryId} ({EventKey}, {Channel}, {Urgency}) dead-lettered: {Error}",
                delivery.Id,
                delivery.EventKey,
                delivery.Channel,
                delivery.Urgency,
                error);
        }
    }
}

/// <summary>Drives <see cref="DeliveryDispatcher"/> on a timer.</summary>
public sealed class DeliveryDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DispatcherOptions> _options;
    private readonly ILogger<DeliveryDispatcherService> _logger;

    public DeliveryDispatcherService(
        IServiceScopeFactory scopeFactory,
        IOptions<DispatcherOptions> options,
        ILogger<DeliveryDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<DeliveryDispatcher>();

                var sent = await dispatcher.DispatchOnceAsync(stoppingToken);

                // A full batch means more is waiting, so keep draining rather than sleeping.
                // At 7am the deferred backlog releases all at once.
                if (sent >= _options.Value.BatchSize)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Notification dispatch failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a fault.
            }
        }
    }
}
