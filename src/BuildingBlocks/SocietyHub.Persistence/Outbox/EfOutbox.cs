using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SocietyHub.Contracts;

namespace SocietyHub.Persistence.Outbox;

/// <summary>
/// Stages integration events in the caller's own <see cref="DbContext"/>, so they are
/// committed by the same <c>SaveChanges</c> that commits the state change.
/// </summary>
public sealed class EfOutbox : IOutbox
{
    private readonly DbContext _context;
    private readonly TimeProvider _timeProvider;

    public EfOutbox(DbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public void Enqueue(IntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        _context.Set<OutboxMessage>().Add(new OutboxMessage
        {
            // The row and the message share an id, so a consumer deduplicating on EventId is
            // deduplicating against the same value an operator sees in the outbox table.
            Id = integrationEvent.EventId,
            EventType = IntegrationEventSerializer.ResolveTypeName(integrationEvent),
            Payload = IntegrationEventSerializer.Serialize(integrationEvent),
            SocietyId = integrationEvent.SocietyId,
            OccurredAtUtc = integrationEvent.OccurredAtUtc,
            // Eligible from the moment it is staged, taken from the clock rather than from
            // OccurredAtUtc. An event stamped slightly in the future by a skewed clock would
            // otherwise sit undelivered until real time caught up with it.
            NextAttemptAtUtc = _timeProvider.GetUtcNow(),
            CorrelationId = integrationEvent.CorrelationId,
        });
    }
}

/// <summary>
/// Maps the outbox table. Call from <c>OnModelCreating</c> in every service that publishes.
/// </summary>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    /// <summary>
    /// Stores the timestamps as UTC <see cref="DateTime"/> rather than
    /// <see cref="DateTimeOffset"/>.
    ///
    /// Two reasons, and the second is the one that bites. Every value on this table is UTC by
    /// construction, so the offset is always zero and carrying it is dead weight. More
    /// importantly the SQLite provider cannot translate a <c>DateTimeOffset</c> comparison,
    /// so the dispatcher's <c>NextAttemptAtUtc &lt;= now</c> predicate fails to translate —
    /// which would leave the outbox testable only against SQL Server, exactly the sort of
    /// dependency that makes a test suite too slow to run on every change.
    /// </summary>
    private static readonly ValueConverter<DateTimeOffset, DateTime> UtcConverter = new(
        offset => offset.UtcDateTime,
        utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    private static readonly ValueConverter<DateTimeOffset?, DateTime?> NullableUtcConverter = new(
        offset => offset == null ? null : offset.Value.UtcDateTime,
        utc => utc == null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)));

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(m => m.Id);

        // Not generated: the id is the event's own EventId, assigned before insert.
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.EventType).HasMaxLength(512).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.SocietyId).IsRequired();
        builder.Property(m => m.CorrelationId).HasMaxLength(128);

        builder.Property(m => m.OccurredAtUtc).HasConversion(UtcConverter).IsRequired();
        builder.Property(m => m.NextAttemptAtUtc).HasConversion(UtcConverter).IsRequired();
        builder.Property(m => m.ProcessedAtUtc).HasConversion(NullableUtcConverter);

        // Bounded so a stack trace cannot bloat the row; the full detail belongs in logs.
        builder.Property(m => m.LastError).HasMaxLength(2000);

        // Covers the dispatcher's only query, leading with the eligibility column it ranges on.
        //
        // Worth narrowing to a filtered index once the SQL Server migration exists —
        // WHERE ProcessedAtUtc IS NULL AND IsPoisoned = 0 — because at roughly 350k events a
        // day the processed rows outnumber the pending ones by orders of magnitude within a
        // week, and every poll would otherwise seek through history it can never return.
        // Left unfiltered here so the same model builds on SQLite for tests.
        builder
            .HasIndex(m => new { m.NextAttemptAtUtc, m.OccurredAtUtc })
            .HasDatabaseName("IX_OutboxMessages_Pending");

        // Lets an operator find everything stuck for one society without a table scan.
        builder.HasIndex(m => new { m.SocietyId, m.IsPoisoned });
    }
}
