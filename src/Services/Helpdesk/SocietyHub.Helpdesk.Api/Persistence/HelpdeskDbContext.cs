using Microsoft.EntityFrameworkCore;
using SocietyHub.Helpdesk.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Helpdesk.Api.Persistence;

/// <summary>
/// Allocates the next human-readable ticket number for a society and year.
///
/// A dedicated counter row rather than <c>MAX(TicketNumber) + 1</c>, which races: two
/// residents complaining at the same instant would both read the same maximum and both get
/// CMP-2026-00412. Incrementing a row takes a lock for the duration of the transaction, so
/// the sequence is gapless and unique per society.
/// </summary>
public sealed class TicketCounter
{
    public Guid SocietyId { get; set; }

    public int Year { get; set; }

    public int LastNumber { get; set; }
}

/// <summary>
/// The Helpdesk service's store.
///
/// Inherits <see cref="TenantDbContext"/>, so every <c>ITenantScoped</c> entity is filtered
/// automatically. Volume here is modest — roughly 500 complaints a day platform-wide against
/// the Gate service's 210,000 entries — so the indexes optimise for the two screens people
/// actually look at: a resident's own tickets, and the committee's overdue list.
/// </summary>
public sealed class HelpdeskDbContext : TenantDbContext
{
    public HelpdeskDbContext(DbContextOptions<HelpdeskDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<Complaint> Complaints => Set<Complaint>();

    public DbSet<ComplaintNote> ComplaintNotes => Set<ComplaintNote>();

    public DbSet<ComplaintAttachment> ComplaintAttachments => Set<ComplaintAttachment>();

    public DbSet<TicketCounter> TicketCounters => Set<TicketCounter>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<Complaint>(complaint =>
        {
            complaint.ToTable("Complaints");
            complaint.Property(c => c.TicketNumber).HasMaxLength(32).IsRequired();
            complaint.Property(c => c.Title).HasMaxLength(200).IsRequired();
            complaint.Property(c => c.Description).HasMaxLength(4000).IsRequired();
            complaint.Property(c => c.Resolution).HasMaxLength(4000);
            complaint.Property(c => c.AssignedToName).HasMaxLength(200);
            complaint.Property(c => c.RatingComment).HasMaxLength(1000);

            complaint.Property(c => c.Category).HasConversion<string>().HasMaxLength(20);
            complaint.Property(c => c.Priority).HasConversion<string>().HasMaxLength(20);
            complaint.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);

            // Two people can act on a ticket at once — a resident closing it while the
            // assignee resolves it. Without this the later write silently wins.
            complaint.Property(c => c.Version).IsRowVersion();

            complaint.HasMany(c => c.Notes)
                     .WithOne()
                     .HasForeignKey(n => n.ComplaintId)
                     .OnDelete(DeleteBehavior.Cascade);

            complaint.HasMany(c => c.Attachments)
                     .WithOne()
                     .HasForeignKey(a => a.ComplaintId)
                     .OnDelete(DeleteBehavior.Cascade);

            complaint.HasIndex(c => new { c.SocietyId, c.TicketNumber })
                     .IsUnique()
                     .HasDatabaseName("IX_Complaints_Ticket");

            // The resident's own list.
            complaint.HasIndex(c => new { c.FlatId, c.Status, c.RaisedAtUtc })
                     .HasDatabaseName("IX_Complaints_Flat_Status");

            // The sweeper's query and the committee's overdue screen. Leading with status
            // keeps it to open tickets, which are a small fraction of the table.
            complaint.HasIndex(c => new { c.Status, c.SlaDueAtUtc })
                     .HasDatabaseName("IX_Complaints_Sla");
        });

        builder.Entity<ComplaintNote>(note =>
        {
            note.ToTable("ComplaintNotes");
            note.Property(n => n.Body).HasMaxLength(4000).IsRequired();

            note.HasIndex(n => new { n.ComplaintId, n.CreatedAtUtc })
                .HasDatabaseName("IX_Notes_Complaint");
        });

        builder.Entity<ComplaintAttachment>(attachment =>
        {
            attachment.ToTable("ComplaintAttachments");
            attachment.Property(a => a.BlobKey).HasMaxLength(400).IsRequired();
            attachment.Property(a => a.FileName).HasMaxLength(260).IsRequired();
            attachment.Property(a => a.ContentType).HasMaxLength(120).IsRequired();
        });

        builder.Entity<TicketCounter>(counter =>
        {
            counter.ToTable("TicketCounters");

            // Composite key rather than a surrogate: the pair *is* the identity, and it makes
            // the allocating update a single-row seek.
            counter.HasKey(c => new { c.SocietyId, c.Year });
        });

        base.OnModelCreating(builder);
    }

    /// <summary>
    /// Allocates the next ticket number for the society, e.g. <c>CMP-2026-00412</c>.
    ///
    /// Must be called inside the same transaction as the complaint insert, so a failed insert
    /// does not consume a number and leave a gap.
    /// </summary>
    public async Task<string> NextTicketNumberAsync(
        Guid societyId,
        int year,
        CancellationToken cancellationToken = default)
    {
        var counter = await TicketCounters
            .SingleOrDefaultAsync(c => c.SocietyId == societyId && c.Year == year, cancellationToken);

        if (counter is null)
        {
            counter = new TicketCounter { SocietyId = societyId, Year = year, LastNumber = 0 };
            TicketCounters.Add(counter);
        }

        counter.LastNumber++;

        return $"CMP-{year}-{counter.LastNumber:D5}";
    }
}
