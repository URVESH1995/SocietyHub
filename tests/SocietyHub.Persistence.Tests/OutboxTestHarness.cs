using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocietyHub.Contracts;
using SocietyHub.Contracts.Helpdesk;
using SocietyHub.Persistence.Outbox;

namespace SocietyHub.Persistence.Tests;

/// <summary>Minimal context standing in for any service that publishes.</summary>
public sealed class OutboxTestDbContext : DbContext
{
    public OutboxTestDbContext(DbContextOptions<OutboxTestDbContext> options) : base(options)
    {
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
}

/// <summary>Records what it was asked to publish, and can be told to fail.</summary>
public sealed class RecordingPublisher : IIntegrationEventPublisher
{
    private readonly List<IntegrationEvent> _published = [];

    public IReadOnlyList<IntegrationEvent> Published => _published;

    /// <summary>When set, every publish throws this. Simulates a broker outage.</summary>
    public Exception? FailWith { get; set; }

    public Task PublishAsync(
        IntegrationEvent integrationEvent,
        CancellationToken cancellationToken = default)
    {
        if (FailWith is not null)
        {
            return Task.FromException(FailWith);
        }

        _published.Add(integrationEvent);
        return Task.CompletedTask;
    }
}

public static class TestEvents
{
    public static ComplaintRaised Complaint(
        Guid societyId,
        string ticket = "CMP-0001",
        DateTimeOffset? occurredAt = null) => new()
    {
        SocietyId = societyId,
        OccurredAtUtc = occurredAt ?? DateTimeOffset.UtcNow,
        ComplaintId = Guid.CreateVersion7(),
        TicketNumber = ticket,
        FlatId = Guid.CreateVersion7(),
        RaisedByUserId = Guid.CreateVersion7(),
        Category = "Plumbing",
        Title = "Tap leaking in kitchen",
        Priority = "Normal",
        SlaDueAtUtc = (occurredAt ?? DateTimeOffset.UtcNow).AddHours(24),
    };
}

/// <summary>Builds a dispatcher over an in-memory SQLite database.</summary>
public static class OutboxHarness
{
    public static OutboxDispatcher Dispatcher(
        OutboxTestDbContext context,
        IIntegrationEventPublisher publisher,
        TimeProvider timeProvider,
        OutboxOptions? options = null) =>
        new(context,
            publisher,
            Options.Create(options ?? new OutboxOptions()),
            timeProvider,
            NullLogger<OutboxDispatcher>.Instance);
}
