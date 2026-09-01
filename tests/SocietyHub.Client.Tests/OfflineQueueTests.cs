using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SocietyHub.Client.Shared.Api;

namespace SocietyHub.Client.Tests;

/// <summary>
/// The offline queue is the only thing standing between a dropped connection and a gate
/// falling back to the paper register. Every property asserted here is one that, if wrong,
/// produces either a duplicate entry or a lost one — and both are the kind of failure a
/// society only discovers when it needs the log.
/// </summary>
public sealed class OfflineQueueTests
{
    private sealed class InMemoryQueueStore : IQueueStore
    {
        private IReadOnlyList<QueuedAction> _actions = [];

        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<QueuedAction>> LoadAsync() => Task.FromResult(_actions);

        public Task SaveAsync(IReadOnlyList<QueuedAction> actions)
        {
            // Copied, not aliased. Holding the live list would make the store agree with the
            // queue even when persistence was never actually called.
            _actions = [.. actions];
            SaveCount++;

            return Task.CompletedTask;
        }
    }

    private static OfflineQueue NewQueue(
        InMemoryQueueStore store, OfflineQueueOptions? options = null) =>
        new(store, options ?? new OfflineQueueOptions(), NullLogger<OfflineQueue>.Instance);

    [Fact]
    public async Task An_enqueued_action_survives_a_restart()
    {
        var store = new InMemoryQueueStore();

        var first = NewQueue(store);
        await first.EnqueueAsync("api/v1/gate/visitors/check-in", new { name = "Ravi" }, "Check in Ravi");

        // A second instance over the same store is what the app does after being killed.
        var second = NewQueue(store);
        var pending = await second.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal("Check in Ravi", pending[0].Description);
    }

    [Fact]
    public async Task Each_action_carries_its_own_idempotency_key()
    {
        var queue = NewQueue(new InMemoryQueueStore());

        await queue.EnqueueAsync("api/v1/gate/visitors/check-in", new { n = 1 }, "One");
        await queue.EnqueueAsync("api/v1/gate/visitors/check-in", new { n = 2 }, "Two");

        var pending = await queue.GetPendingAsync();

        Assert.Equal(2, pending.Select(a => a.IdempotencyKey).Distinct().Count());
    }

    [Fact]
    public async Task A_retry_reuses_the_key_minted_when_the_action_was_taken()
    {
        // The property the whole design rests on. If the key were generated at send time, a
        // successful request whose response was lost would be retried as a new action and the
        // visitor would be checked in twice.
        var queue = NewQueue(new InMemoryQueueStore());
        await queue.EnqueueAsync("api/v1/gate/visitors/check-in", new { }, "Check in");

        var keyBefore = (await queue.GetPendingAsync())[0].IdempotencyKey;

        await queue.DrainAsync((_, _) => throw new HttpRequestException("offline"));

        var keyAfter = (await queue.GetPendingAsync())[0].IdempotencyKey;

        Assert.Equal(keyBefore, keyAfter);
    }

    [Fact]
    public async Task The_occurred_time_is_when_the_guard_acted_not_when_it_syncs()
    {
        // A gate log that timestamps a 7am entry as 11am — because that is when the network
        // came back — cannot answer "who came in before the theft".
        var queue = NewQueue(new InMemoryQueueStore());

        var before = DateTimeOffset.UtcNow;
        await queue.EnqueueAsync("api/v1/gate/visitors/check-in", new { }, "Check in");
        var after = DateTimeOffset.UtcNow;

        var occurred = (await queue.GetPendingAsync())[0].OccurredAtUtc;

        Assert.InRange(occurred, before, after);
    }

    [Fact]
    public async Task Draining_sends_in_the_order_the_actions_were_taken()
    {
        // A check-out replayed before its check-in is rejected as a state violation and the
        // entry is lost.
        var queue = NewQueue(new InMemoryQueueStore());

        await queue.EnqueueAsync("a", new { }, "First");
        await queue.EnqueueAsync("b", new { }, "Second");
        await queue.EnqueueAsync("c", new { }, "Third");

        var sent = new List<string>();

        var result = await queue.DrainAsync((action, _) =>
        {
            sent.Add(action.Description);
            return Task.CompletedTask;
        });

        Assert.Equal(["First", "Second", "Third"], sent);
        Assert.Equal(3, result.Sent);
        Assert.True(result.Completed);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task A_transient_failure_stops_the_drain_and_keeps_the_order()
    {
        // Skipping past a stuck action would send later ones out of sequence. Stopping keeps
        // the queue causally correct at the cost of waiting.
        var queue = NewQueue(new InMemoryQueueStore());

        await queue.EnqueueAsync("a", new { }, "First");
        await queue.EnqueueAsync("b", new { }, "Second");

        var attempts = 0;

        var result = await queue.DrainAsync((_, _) =>
        {
            attempts++;
            throw new HttpRequestException("offline");
        });

        Assert.Equal(1, attempts);
        Assert.False(result.Completed);
        Assert.Equal(0, result.Sent);
        Assert.Equal(2, queue.Depth);

        var pending = await queue.GetPendingAsync();
        Assert.Equal("First", pending[0].Description);
        Assert.Equal(1, pending[0].AttemptCount);
    }

    [Fact]
    public async Task A_permanent_rejection_is_parked_so_it_cannot_block_the_queue()
    {
        // The server understood it and said no. Retrying will not change that, and leaving it
        // at the head would stop every later entry forever.
        var queue = NewQueue(new InMemoryQueueStore());

        await queue.EnqueueAsync("a", new { }, "Rejected");
        await queue.EnqueueAsync("b", new { }, "Fine");

        var result = await queue.DrainAsync((action, _) =>
            action.Description == "Rejected"
                ? throw new ApiException(HttpStatusCode.Conflict, "gate.bad_state", "No.")
                : Task.CompletedTask);

        Assert.True(result.Completed);
        Assert.Equal(1, result.Sent);
        Assert.Single(result.Parked);
        Assert.Equal("Rejected", result.Parked[0].Description);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task A_parked_action_is_reported_rather_than_discarded_silently()
    {
        // A rejected entry means someone got through the gate with no record. The guard is the
        // only person who can still do something about it, so they have to be told.
        var queue = NewQueue(new InMemoryQueueStore());
        await queue.EnqueueAsync("a", new { }, "Check in Ravi");

        var result = await queue.DrainAsync((_, _) =>
            throw new ApiException(HttpStatusCode.BadRequest, "gate.invalid", "Bad pass code."));

        Assert.Single(result.Parked);
        Assert.Contains("Bad pass code", result.Parked[0].LastError);
    }

    [Fact]
    public async Task An_action_that_keeps_failing_is_eventually_parked_not_retried_forever()
    {
        var options = new OfflineQueueOptions { MaxAttempts = 3 };
        var queue = NewQueue(new InMemoryQueueStore(), options);

        await queue.EnqueueAsync("a", new { }, "Stuck");

        SyncResult result;

        do
        {
            result = await queue.DrainAsync((_, _) => throw new HttpRequestException("offline"));
        }
        while (!result.Completed);

        Assert.Single(result.Parked);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task A_full_queue_refuses_rather_than_dropping_the_oldest_entry()
    {
        // A guard told the queue is full can act — call the office, use paper deliberately.
        // A guard whose first entries silently vanished has no idea anything is wrong.
        var queue = NewQueue(new InMemoryQueueStore(), new OfflineQueueOptions { MaxDepth = 2 });

        Assert.True(await queue.EnqueueAsync("a", new { }, "First"));
        Assert.True(await queue.EnqueueAsync("b", new { }, "Second"));
        Assert.False(await queue.EnqueueAsync("c", new { }, "Third"));

        var pending = await queue.GetPendingAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal("First", pending[0].Description);
    }

    [Fact]
    public async Task Every_change_is_persisted_before_the_call_returns()
    {
        // If enqueue only wrote to memory, an app killed by Android between the guard tapping
        // and the next sync would lose the entry — which is precisely the moment the queue is
        // supposed to be protecting.
        var store = new InMemoryQueueStore();
        var queue = NewQueue(store);

        await queue.EnqueueAsync("a", new { }, "First");

        Assert.Equal(1, store.SaveCount);
        Assert.Single(await store.LoadAsync());
    }
}
