using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SocietyHub.Persistence.Outbox;

public sealed class OutboxOptions
{
    /// <summary>How often to look for pending messages.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages per pass. Bounded so one pass cannot hold a connection all day.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Attempts before a message is poisoned and stops being retried.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Base for exponential backoff: 2s, 4s, 8s … capped by <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Drives <see cref="OutboxDispatcher"/> on a timer.
///
/// Delivery is <b>at-least-once</b> and cannot be otherwise: the broker may accept a message
/// and the process die before the row is marked processed, so it is sent again on restart.
/// Every consumer must therefore deduplicate on the event id. Extra replicas widen the
/// duplicate window rather than breaking anything, which is why polls are jittered — and why
/// the Redis lease from P1-10 is worth adding on top once it exists.
/// </summary>
public sealed class OutboxProcessor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OutboxOptions> _options;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
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
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

                var published = await dispatcher.DispatchOnceAsync(stoppingToken);

                // A full batch means more is probably waiting, so keep draining instead of
                // sleeping. This is what stops a backlog clearing at one batch per interval.
                if (published >= _options.Value.BatchSize)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let the loop die. A database outage must pause publishing, not stop
                // it silently until somebody notices and restarts the service.
                _logger.LogError(ex, "Outbox pass failed; retrying after the poll interval.");
            }

            await SafeDelayAsync(Jittered(_options.Value.PollInterval), stoppingToken);
        }
    }

    /// <summary>
    /// Spreads replicas apart so they do not all wake at the same instant and read the same
    /// rows. Cheap, and it noticeably reduces duplicate publishes.
    /// </summary>
    private static TimeSpan Jittered(TimeSpan interval) =>
        interval + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault.
        }
    }
}
