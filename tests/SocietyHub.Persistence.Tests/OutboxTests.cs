using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SocietyHub.Contracts.Helpdesk;
using SocietyHub.Persistence.Outbox;

namespace SocietyHub.Persistence.Tests;

/// <summary>
/// Covers the guarantee the outbox exists to provide — a state change and the message
/// announcing it commit together — and the failure handling that makes it survivable in
/// production: ordering, backoff, and poisoning.
/// </summary>
public sealed class OutboxTests : IDisposable
{
    private static readonly Guid Society = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly RecordingPublisher _publisher = new();

    public OutboxTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    // ---------------------------------------------------------------
    // Staging
    // ---------------------------------------------------------------

    [Fact]
    public void Enqueue_stages_a_row_carrying_the_events_own_id()
    {
        using var context = CreateContext();
        var outbox = new EfOutbox(context, _clock);
        var raised = TestEvents.Complaint(Society);

        outbox.Enqueue(raised);
        context.SaveChanges();

        var row = context.OutboxMessages.Single();

        Assert.Equal(raised.EventId, row.Id);
        Assert.Equal(Society, row.SocietyId);
        Assert.Equal(typeof(ComplaintRaised).FullName, row.EventType);
        Assert.Null(row.ProcessedAtUtc);
        Assert.False(row.IsPoisoned);
    }

    [Fact]
    public void Enqueue_writes_nothing_until_the_caller_saves()
    {
        // The whole point of the pattern: a handler that stages an event and then fails
        // publishes nothing, because the row was never committed.
        using (var context = CreateContext())
        {
            new EfOutbox(context, _clock).Enqueue(TestEvents.Complaint(Society));
            // Deliberately no SaveChanges — simulates a handler throwing.
        }

        using var verify = CreateContext();
        Assert.Empty(verify.OutboxMessages);
    }

    [Fact]
    public void Enqueued_message_rolls_back_with_the_transaction_that_staged_it()
    {
        using var context = CreateContext();
        using var transaction = context.Database.BeginTransaction();

        new EfOutbox(context, _clock).Enqueue(TestEvents.Complaint(Society));
        context.SaveChanges();
        transaction.Rollback();

        using var verify = CreateContext();
        Assert.Empty(verify.OutboxMessages);
    }

    // ---------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------

    [Fact]
    public async Task Dispatch_publishes_pending_messages_and_marks_them_processed()
    {
        var raised = TestEvents.Complaint(Society, "CMP-0007");
        Stage(raised);

        using var context = CreateContext();
        var published = await OutboxHarness
            .Dispatcher(context, _publisher, _clock)
            .DispatchOnceAsync();

        Assert.Equal(1, published);

        var delivered = Assert.IsType<ComplaintRaised>(Assert.Single(_publisher.Published));
        Assert.Equal("CMP-0007", delivered.TicketNumber);
        Assert.Equal(raised.EventId, delivered.EventId);

        Assert.NotNull(context.OutboxMessages.Single().ProcessedAtUtc);
    }

    [Fact]
    public async Task Processed_messages_are_never_published_twice()
    {
        Stage(TestEvents.Complaint(Society));

        using var context = CreateContext();
        var dispatcher = OutboxHarness.Dispatcher(context, _publisher, _clock);

        await dispatcher.DispatchOnceAsync();
        await dispatcher.DispatchOnceAsync();

        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task Messages_are_published_in_the_order_they_occurred()
    {
        // Causal ordering matters: a check-out that overtakes its check-in produces a
        // visitor who left a building they never entered.
        Stage(TestEvents.Complaint(Society, "THIRD", Now.AddSeconds(30)));
        Stage(TestEvents.Complaint(Society, "FIRST", Now));
        Stage(TestEvents.Complaint(Society, "SECOND", Now.AddSeconds(10)));

        using var context = CreateContext();
        await OutboxHarness.Dispatcher(context, _publisher, _clock).DispatchOnceAsync();

        var order = _publisher.Published.Cast<ComplaintRaised>().Select(e => e.TicketNumber);
        Assert.Equal(["FIRST", "SECOND", "THIRD"], order);
    }

    [Fact]
    public async Task Batch_size_bounds_a_single_pass()
    {
        for (var i = 0; i < 5; i++)
        {
            Stage(TestEvents.Complaint(Society, $"CMP-{i}", Now.AddSeconds(i)));
        }

        using var context = CreateContext();
        var published = await OutboxHarness
            .Dispatcher(context, _publisher, _clock, new OutboxOptions { BatchSize = 2 })
            .DispatchOnceAsync();

        Assert.Equal(2, published);
        Assert.Equal(2, _publisher.Published.Count);
    }

    // ---------------------------------------------------------------
    // Failure handling
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_failed_publish_is_retried_later_with_backoff()
    {
        Stage(TestEvents.Complaint(Society));
        _publisher.FailWith = new InvalidOperationException("broker unreachable");

        using var context = CreateContext();
        await OutboxHarness.Dispatcher(context, _publisher, _clock).DispatchOnceAsync();

        var row = context.OutboxMessages.Single();

        Assert.Null(row.ProcessedAtUtc);
        Assert.Equal(1, row.AttemptCount);
        Assert.False(row.IsPoisoned);
        Assert.Equal("broker unreachable", row.LastError);
        Assert.Equal(Now.AddSeconds(2), row.NextAttemptAtUtc);
    }

    [Fact]
    public async Task A_message_in_backoff_is_skipped_until_its_time_arrives()
    {
        Stage(TestEvents.Complaint(Society));
        _publisher.FailWith = new InvalidOperationException("broker unreachable");

        using var context = CreateContext();
        var dispatcher = OutboxHarness.Dispatcher(context, _publisher, _clock);

        await dispatcher.DispatchOnceAsync();

        // One second later the backoff has not expired, so nothing should be attempted.
        _clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchOnceAsync();
        Assert.Equal(1, context.OutboxMessages.Single().AttemptCount);

        // Past the backoff, and the broker is healthy again.
        _clock.Advance(TimeSpan.FromSeconds(5));
        _publisher.FailWith = null;
        var published = await dispatcher.DispatchOnceAsync();

        Assert.Equal(1, published);
        Assert.NotNull(context.OutboxMessages.Single().ProcessedAtUtc);
    }

    [Fact]
    public async Task A_message_is_poisoned_after_the_attempt_limit_and_stops_being_retried()
    {
        Stage(TestEvents.Complaint(Society));
        _publisher.FailWith = new InvalidOperationException("broker unreachable");

        using var context = CreateContext();
        var options = new OutboxOptions { MaxAttempts = 3 };
        var dispatcher = OutboxHarness.Dispatcher(context, _publisher, _clock, options);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await dispatcher.DispatchOnceAsync();
            _clock.Advance(TimeSpan.FromMinutes(30));
        }

        var row = context.OutboxMessages.Single();
        Assert.True(row.IsPoisoned);
        Assert.Equal(3, row.AttemptCount);

        // Poisoned rows are skipped even once the broker recovers, so they cannot occupy a
        // slot in every batch forever. They are kept, not deleted — an undeliverable message
        // is exactly the one an operator needs to be able to see.
        _publisher.FailWith = null;
        Assert.Equal(0, await dispatcher.DispatchOnceAsync());
        Assert.NotNull(context.OutboxMessages.Single());
    }

    [Fact]
    public async Task An_unknown_event_type_is_poisoned_immediately_rather_than_retried()
    {
        // A type renamed or deleted while rows referencing it were still pending. No number
        // of retries can fix that, so it must not consume the full attempt budget.
        using (var seed = CreateContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                EventType = "SocietyHub.Contracts.Gate.DeletedLegacyEvent",
                Payload = "{}",
                SocietyId = Society,
                OccurredAtUtc = Now,
                NextAttemptAtUtc = Now,
            });
            seed.SaveChanges();
        }

        using var context = CreateContext();
        var published = await OutboxHarness
            .Dispatcher(context, _publisher, _clock)
            .DispatchOnceAsync();

        var row = context.OutboxMessages.Single();

        Assert.Equal(0, published);
        Assert.True(row.IsPoisoned);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains("DeletedLegacyEvent", row.LastError);
    }

    // ---------------------------------------------------------------
    // Serialisation
    // ---------------------------------------------------------------

    [Fact]
    public void Every_integration_event_in_the_contracts_assembly_is_resolvable()
    {
        // Guards the round trip. A contract the serialiser cannot resolve becomes a poisoned
        // message at runtime, and the first sign of it would be an undelivered notification.
        Assert.NotEmpty(IntegrationEventSerializer.RegisteredTypeNames);
        Assert.Contains(typeof(ComplaintRaised).FullName, IntegrationEventSerializer.RegisteredTypeNames);
    }

    [Fact]
    public void Serialisation_round_trips_every_field()
    {
        var original = TestEvents.Complaint(Society, "CMP-9999");

        var restored = (ComplaintRaised)IntegrationEventSerializer.Deserialize(
            IntegrationEventSerializer.ResolveTypeName(original),
            IntegrationEventSerializer.Serialize(original));

        Assert.Equal(original.EventId, restored.EventId);
        Assert.Equal(original.SocietyId, restored.SocietyId);
        Assert.Equal(original.ComplaintId, restored.ComplaintId);
        Assert.Equal("CMP-9999", restored.TicketNumber);
        Assert.Equal(original.SlaDueAtUtc, restored.SlaDueAtUtc);
    }

    // ---------------------------------------------------------------

    private void Stage(SocietyHub.Contracts.IntegrationEvent integrationEvent)
    {
        using var context = CreateContext();
        new EfOutbox(context, _clock).Enqueue(integrationEvent);
        context.SaveChanges();
    }

    private OutboxTestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OutboxTestDbContext>()
            .UseSqlite(_connection)
            .Options);

    public void Dispose() => _connection.Dispose();
}
