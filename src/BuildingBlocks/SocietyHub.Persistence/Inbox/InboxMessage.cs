using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SocietyHub.Persistence.Inbox;

/// <summary>
/// A record that one consumer has already handled one event.
///
/// The outbox guarantees a message is sent at least once; this is the other half, and
/// without it that guarantee is a liability. A duplicate <c>ComplaintRaised</c> means a second
/// SMS to a resident at midnight. A duplicate <c>VisitorCheckedIn</c> means a second visitor
/// in the gate log who never existed. A duplicate payment capture means charging someone
/// twice.
///
/// The key is the pair, not the event id alone, because one event is legitimately handled by
/// several consumers: Notification sends a push, Reporting updates a projection, and each must
/// process it exactly once without blocking the other.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>The <c>EventId</c> carried on the integration event.</summary>
    public required Guid EventId { get; init; }

    /// <summary>
    /// Stable identifier for the consumer, not the CLR type name — renaming a consumer class
    /// must not make it reprocess every event it has already handled.
    /// </summary>
    public required string ConsumerName { get; init; }

    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>
    /// Denormalised for triage and for purging a society's history on offboarding.
    /// </summary>
    public required Guid SocietyId { get; init; }
}

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    private static readonly ValueConverter<DateTimeOffset, DateTime> UtcConverter = new(
        offset => offset.UtcDateTime,
        utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages");

        // The composite key is the deduplication mechanism itself. Claiming is an INSERT, and
        // the database rejects the second one — no read-then-write race, no distributed lock,
        // and correct even with several consumer replicas racing on the same message.
        builder.HasKey(m => new { m.EventId, m.ConsumerName });

        builder.Property(m => m.ConsumerName).HasMaxLength(200);
        builder.Property(m => m.ReceivedAtUtc).HasConversion(UtcConverter).IsRequired();
        builder.Property(m => m.SocietyId).IsRequired();

        // Supports retention sweeps, which are what stop this table growing without bound.
        builder.HasIndex(m => m.ReceivedAtUtc);
    }
}
