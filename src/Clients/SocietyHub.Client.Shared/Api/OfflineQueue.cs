using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SocietyHub.Client.Shared.Api;

/// <summary>Where queued work survives the app being killed.</summary>
public interface IQueueStore
{
    Task<IReadOnlyList<QueuedAction>> LoadAsync();

    Task SaveAsync(IReadOnlyList<QueuedAction> actions);
}

/// <summary>
/// One action a guard took while the tablet could not reach the server.
/// </summary>
public sealed record QueuedAction
{
    public required Guid Id { get; init; }

    /// <summary>What to replay. A route and a JSON body, not a delegate — a closure cannot be written to disk.</summary>
    public required string Path { get; init; }

    public required string JsonBody { get; init; }

    /// <summary>
    /// The idempotency key, generated when the action was taken rather than when it is sent.
    ///
    /// This is what makes the queue safe. If a send succeeds but the response is lost, the
    /// retry carries the same key and the server recognises it as the same action — so a
    /// visitor is checked in once, not twice.
    /// </summary>
    public required Guid IdempotencyKey { get; init; }

    /// <summary>When the guard actually did this, which is not when it is sent.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    public int AttemptCount { get; init; }

    public string? LastError { get; init; }

    /// <summary>
    /// A human-readable label for the queue screen. A guard looking at "3 waiting to sync"
    /// needs to know which three, or they will do them again on paper.
    /// </summary>
    public required string Description { get; init; }
}

public sealed class OfflineQueueOptions
{
    /// <summary>
    /// How many actions the queue holds before it refuses more.
    ///
    /// A gate does roughly 200 entries a day. Five hundred is more than two days of total
    /// outage, past which the honest answer is that something is badly wrong and a guard
    /// should be told rather than handed a queue that will never drain.
    /// </summary>
    public int MaxDepth { get; set; } = 500;

    /// <summary>
    /// Attempts before an action is parked for a human.
    ///
    /// Parked, never dropped. A dropped gate entry is a visitor with no record of entering,
    /// which is precisely what the society bought this system to prevent.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;
}

/// <summary>
/// The Guard app's offline queue.
///
/// A guard tablet sits at a gate on a connection that drops — a power cut, a router reboot, a
/// contractor cutting a cable. When it does, the guard cannot stop working: vehicles are
/// arriving. The alternative to this queue is the paper register, and once a gate falls back
/// to paper it tends to stay there, which is how a digital gate log silently goes stale.
///
/// Two properties make it safe rather than merely convenient:
///
/// Order is preserved. A check-out replayed before its check-in is rejected by the server as
/// a state violation, and the entry is lost.
///
/// Idempotency keys are minted when the action is taken, not when it is sent. Without that,
/// every retry after a lost response creates a duplicate.
/// </summary>
public sealed class OfflineQueue
{
    private readonly IQueueStore _store;
    private readonly OfflineQueueOptions _options;
    private readonly ILogger<OfflineQueue> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private List<QueuedAction> _actions = [];
    private bool _loaded;

    public OfflineQueue(
        IQueueStore store, OfflineQueueOptions options, ILogger<OfflineQueue> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>Fires when the depth changes, so the shell can update its badge.</summary>
    public event Action<int>? DepthChanged;

    public int Depth => _actions.Count;

    public async Task<IReadOnlyList<QueuedAction>> GetPendingAsync()
    {
        await EnsureLoadedAsync();
        return _actions;
    }

    public async Task<bool> EnqueueAsync<TBody>(string path, TBody body, string description)
    {
        await EnsureLoadedAsync();
        await _mutex.WaitAsync();

        try
        {
            if (_actions.Count >= _options.MaxDepth)
            {
                // Refused rather than silently dropping the oldest. A guard who is told the
                // queue is full can act — call the office, use paper deliberately. A guard
                // whose first entries vanished has no idea anything is wrong.
                _logger.LogError(
                    "Offline queue is full at {Depth} actions. Refusing {Path}.",
                    _actions.Count,
                    path);

                return false;
            }

            _actions.Add(new QueuedAction
            {
                Id = Guid.CreateVersion7(),
                Path = path,
                JsonBody = JsonSerializer.Serialize(body),
                IdempotencyKey = Guid.CreateVersion7(),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Description = description,
            });

            await PersistAsync();
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Drains the queue in order, stopping at the first action that cannot be sent.
    ///
    /// Stopping rather than skipping is the point. The actions are causally related — a
    /// check-out only makes sense after its check-in — so sending later ones past a stuck
    /// earlier one produces server-side rejections that look like data corruption.
    /// </summary>
    public async Task<SyncResult> DrainAsync(
        Func<QueuedAction, CancellationToken, Task> send, CancellationToken ct = default)
    {
        await EnsureLoadedAsync();
        await _mutex.WaitAsync(ct);

        try
        {
            var sent = 0;
            var parked = new List<QueuedAction>();

            while (_actions.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var action = _actions[0];

                try
                {
                    await send(action, ct);

                    _actions.RemoveAt(0);
                    sent++;
                }
                catch (ApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest
                                                  or System.Net.HttpStatusCode.Conflict
                                                  or System.Net.HttpStatusCode.UnprocessableEntity)
                {
                    // The server understood it and said no. Retrying will not change that, so
                    // the action is parked for a human rather than blocking everything behind
                    // it forever. This is the one case where skipping is right.
                    _logger.LogWarning(
                        "Parking queued action {Id} ({Description}): {Message}",
                        action.Id,
                        action.Description,
                        ex.Message);

                    parked.Add(action with { LastError = ex.Message });
                    _actions.RemoveAt(0);
                }
                catch (Exception ex)
                {
                    // Still offline, or the server is down. Keep the action and stop —
                    // everything behind it stays in order.
                    var attempted = action with
                    {
                        AttemptCount = action.AttemptCount + 1,
                        LastError = ex.Message,
                    };

                    if (attempted.AttemptCount >= _options.MaxAttempts)
                    {
                        _logger.LogError(
                            ex,
                            "Queued action {Id} ({Description}) failed {Count} times. Parking it.",
                            action.Id,
                            action.Description,
                            attempted.AttemptCount);

                        parked.Add(attempted);
                        _actions.RemoveAt(0);
                        continue;
                    }

                    _actions[0] = attempted;
                    await PersistAsync();

                    return new SyncResult(sent, _actions.Count, parked, Completed: false);
                }
            }

            await PersistAsync();
            return new SyncResult(sent, _actions.Count, parked, Completed: true);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        await _mutex.WaitAsync();

        try
        {
            if (_loaded)
            {
                return;
            }

            _actions = [.. await _store.LoadAsync()];
            _loaded = true;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task PersistAsync()
    {
        await _store.SaveAsync(_actions);
        DepthChanged?.Invoke(_actions.Count);
    }
}

/// <summary>
/// <paramref name="Parked"/> are actions the server refused permanently. They are reported so
/// a guard can be shown what did not go through, rather than discovering it a week later.
/// </summary>
public sealed record SyncResult(
    int Sent, int Remaining, IReadOnlyList<QueuedAction> Parked, bool Completed);
