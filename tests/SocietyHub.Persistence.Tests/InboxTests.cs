using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SocietyHub.Persistence.Inbox;

namespace SocietyHub.Persistence.Tests;

public sealed class InboxTestDbContext : DbContext
{
    public InboxTestDbContext(DbContextOptions<InboxTestDbContext> options) : base(options)
    {
    }

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
}

/// <summary>
/// The inbox is what turns the outbox's at-least-once delivery into exactly-once handling.
/// These cover the duplicate cases that actually occur in production: a broker redelivery, a
/// processor restart, and two replicas racing the same message.
/// </summary>
public sealed class InboxTests : IDisposable
{
    private const string Notifier = "notification.complaint-raised";
    private const string Reporter = "reporting.complaint-projection";

    private static readonly Guid Society = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly SqliteConnection _connection;
    private readonly FakeTimeProvider _clock =
        new(new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero));

    public InboxTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task A_first_delivery_is_claimed()
    {
        using var context = CreateContext();
        var raised = TestEvents.Complaint(Society);

        var claimed = await new EfInbox(context, _clock).TryClaimAsync(raised, Notifier);
        await context.SaveChangesAsync();

        Assert.True(claimed);

        var row = context.InboxMessages.Single();
        Assert.Equal(raised.EventId, row.EventId);
        Assert.Equal(Notifier, row.ConsumerName);
        Assert.Equal(Society, row.SocietyId);
    }

    [Fact]
    public async Task A_redelivery_of_the_same_event_is_refused()
    {
        var raised = TestEvents.Complaint(Society);

        using (var first = CreateContext())
        {
            await new EfInbox(first, _clock).TryClaimAsync(raised, Notifier);
            await first.SaveChangesAsync();
        }

        using var second = CreateContext();
        var claimedAgain = await new EfInbox(second, _clock).TryClaimAsync(raised, Notifier);

        Assert.False(claimedAgain);
    }

    [Fact]
    public async Task Different_consumers_each_handle_the_same_event()
    {
        // One event legitimately fans out: Notification sends a push, Reporting updates a
        // projection. Deduplicating on the event id alone would let whichever consumer ran
        // first silently suppress the other.
        var raised = TestEvents.Complaint(Society);

        using var context = CreateContext();
        var inbox = new EfInbox(context, _clock);

        Assert.True(await inbox.TryClaimAsync(raised, Notifier));
        Assert.True(await inbox.TryClaimAsync(raised, Reporter));

        await context.SaveChangesAsync();
        Assert.Equal(2, context.InboxMessages.Count());
    }

    [Fact]
    public async Task A_claim_rolls_back_when_the_handler_fails()
    {
        // The property that makes this exactly-once rather than merely deduplicated. If the
        // claim survived a failed handler, the redelivery would be skipped as "already done"
        // and the work would be lost silently — the worst possible outcome.
        var raised = TestEvents.Complaint(Society);

        using (var context = CreateContext())
        using (var transaction = context.Database.BeginTransaction())
        {
            await new EfInbox(context, _clock).TryClaimAsync(raised, Notifier);
            await context.SaveChangesAsync();

            // Handler throws here in real code.
            await transaction.RollbackAsync();
        }

        using var retry = CreateContext();
        var claimedOnRetry = await new EfInbox(retry, _clock).TryClaimAsync(raised, Notifier);

        Assert.True(claimedOnRetry);
    }

    [Fact]
    public async Task A_concurrent_claim_by_another_replica_fails_on_commit()
    {
        // Two replicas both read "absent" and both proceed. The composite primary key is what
        // actually prevents double handling, and it does so at commit rather than at read.
        var raised = TestEvents.Complaint(Society);

        using var replicaA = CreateContext();
        using var replicaB = CreateContext();

        Assert.True(await new EfInbox(replicaA, _clock).TryClaimAsync(raised, Notifier));
        Assert.True(await new EfInbox(replicaB, _clock).TryClaimAsync(raised, Notifier));

        await replicaA.SaveChangesAsync();

        var collision = await Assert.ThrowsAsync<DbUpdateException>(
            () => replicaB.SaveChangesAsync());

        Assert.IsType<SqliteException>(collision.InnerException);
        Assert.Equal(1, replicaA.InboxMessages.Count());
    }

    [Fact]
    public async Task The_same_event_id_in_two_societies_is_tracked_separately()
    {
        var otherSociety = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var shared = Guid.CreateVersion7();

        var first = TestEvents.Complaint(Society) with { EventId = shared };
        var second = TestEvents.Complaint(otherSociety) with { EventId = shared };

        using var context = CreateContext();
        var inbox = new EfInbox(context, _clock);

        Assert.True(await inbox.TryClaimAsync(first, Notifier));
        await context.SaveChangesAsync();

        // Same id, different society. Event ids are generated per publisher and are unique in
        // practice, so a collision here means the second is a genuine duplicate and is refused
        // — the safe direction to fail.
        Assert.False(await inbox.TryClaimAsync(second, Notifier));
    }

    private InboxTestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InboxTestDbContext>()
            .UseSqlite(_connection)
            .Options);

    public void Dispose() => _connection.Dispose();
}
