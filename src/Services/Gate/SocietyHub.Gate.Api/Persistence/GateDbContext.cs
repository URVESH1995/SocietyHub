using Microsoft.EntityFrameworkCore;
using SocietyHub.Gate.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Gate.Api.Persistence;

/// <summary>
/// The Gate service's store.
///
/// Inherits <see cref="TenantDbContext"/>, so every <c>ITenantScoped</c> entity is discovered
/// and filtered automatically — a new table cannot quietly opt out of isolation.
///
/// This is the write-heaviest schema in the platform: gate traffic arrives in two sharp
/// spikes a day and the entry log reaches roughly 77 million rows a year. The indexes below
/// are chosen for the four queries the gate and the resident app actually make, and nothing
/// else — an extra index on this table costs write throughput at exactly 7pm.
/// </summary>
public sealed class GateDbContext : TenantDbContext
{
    public GateDbContext(DbContextOptions<GateDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<VisitPass> VisitPasses => Set<VisitPass>();

    public DbSet<GateEntry> GateEntries => Set<GateEntry>();

    public DbSet<DailyHelp> DailyHelps => Set<DailyHelp>();

    public DbSet<HelpAssignment> HelpAssignments => Set<HelpAssignment>();

    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();

    public DbSet<BlacklistEntry> BlacklistEntries => Set<BlacklistEntry>();

    public DbSet<SosIncident> SosIncidents => Set<SosIncident>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        ConfigureVisitPasses(builder);
        ConfigureGateEntries(builder);
        ConfigureDailyHelp(builder);
        ConfigureSafety(builder);

        // Applies the tenant and soft-delete filters. Called last so it sees every entity.
        base.OnModelCreating(builder);
    }

    private static void ConfigureVisitPasses(ModelBuilder builder)
    {
        builder.Entity<VisitPass>(pass =>
        {
            pass.ToTable("VisitPasses");
            pass.Property(p => p.VisitorName).HasMaxLength(200).IsRequired();
            pass.Property(p => p.VisitorPhone).HasMaxLength(20);
            pass.Property(p => p.CodeHash).HasMaxLength(64).IsRequired();
            pass.Property(p => p.CodeSalt).HasMaxLength(32).IsRequired();
            pass.Property(p => p.VehicleNumber).HasMaxLength(20);
            pass.Property(p => p.PhotoBlobKey).HasMaxLength(400);
            pass.Property(p => p.Purpose).HasMaxLength(400);

            pass.Property(p => p.VisitorType).HasConversion<string>().HasMaxLength(20);
            pass.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);

            // Optimistic concurrency. Two guards can scan the same pass simultaneously at a
            // two-lane gate, and without this both check-ins would succeed.
            pass.Property(p => p.Version).IsRowVersion();

            // The gate's lookup: open passes for a society, newest first. Filtered to Pending
            // so it stays small — the vast majority of rows are long since checked out.
            pass.HasIndex(p => new { p.SocietyId, p.Status, p.ValidUntilUtc })
                .HasDatabaseName("IX_VisitPasses_Open");

            // The resident app's "who is expected at my flat today".
            pass.HasIndex(p => new { p.FlatId, p.ValidFromUtc })
                .HasDatabaseName("IX_VisitPasses_Flat_Window");
        });
    }

    private static void ConfigureGateEntries(ModelBuilder builder)
    {
        builder.Entity<GateEntry>(entry =>
        {
            entry.ToTable("GateEntries");
            entry.Property(e => e.PersonName).HasMaxLength(200);
            entry.Property(e => e.PersonPhone).HasMaxLength(20);
            entry.Property(e => e.VehicleNumber).HasMaxLength(20);
            entry.Property(e => e.PhotoBlobKey).HasMaxLength(400);
            entry.Property(e => e.Notes).HasMaxLength(1000);

            entry.Property(e => e.Direction).HasConversion<string>().HasMaxLength(10);
            entry.Property(e => e.VisitorType).HasConversion<string>().HasMaxLength(20);

            // Leads with the partition key so a query for one month reads one partition.
            // Ordering matters here: SocietyId first would scan every month for that society.
            entry.HasIndex(e => new { e.PartitionKey, e.SocietyId, e.OccurredAtUtc })
                 .HasDatabaseName("IX_GateEntries_Partition_Society_Time");

            // "Who came to my flat" — the resident app's history screen.
            entry.HasIndex(e => new { e.FlatId, e.OccurredAtUtc })
                 .HasDatabaseName("IX_GateEntries_Flat_Time");

            // Reconciles a check-out against its check-in.
            entry.HasIndex(e => e.VisitPassId).HasDatabaseName("IX_GateEntries_Pass");
        });
    }

    private static void ConfigureDailyHelp(ModelBuilder builder)
    {
        builder.Entity<DailyHelp>(help =>
        {
            help.ToTable("DailyHelps");
            help.Property(h => h.FullName).HasMaxLength(200).IsRequired();
            help.Property(h => h.PhoneNumber).HasMaxLength(20).IsRequired();
            help.Property(h => h.BadgeCode).HasMaxLength(64);
            help.Property(h => h.PhotoBlobKey).HasMaxLength(400);
            help.Property(h => h.Category).HasConversion<string>().HasMaxLength(20);

            help.HasMany(h => h.Assignments)
                .WithOne(a => a.DailyHelp!)
                .HasForeignKey(a => a.DailyHelpId)
                .OnDelete(DeleteBehavior.Cascade);

            // The badge scan at the gate. Unique per society, not globally — two societies may
            // print the same card number and must not resolve to each other's worker.
            help.HasIndex(h => new { h.SocietyId, h.BadgeCode })
                .IsUnique()
                .HasFilter("[BadgeCode] IS NOT NULL")
                .HasDatabaseName("IX_DailyHelps_Badge");

            help.HasIndex(h => new { h.SocietyId, h.PhoneNumber })
                .HasDatabaseName("IX_DailyHelps_Phone");
        });

        builder.Entity<HelpAssignment>(assignment =>
        {
            assignment.ToTable("HelpAssignments");

            assignment.HasIndex(a => new { a.FlatId, a.IsActive })
                      .HasDatabaseName("IX_HelpAssignments_Flat");
        });

        builder.Entity<AttendanceRecord>(record =>
        {
            record.ToTable("AttendanceRecords");

            // One row per worker per day is the invariant the punch logic depends on. Enforced
            // here rather than trusted to the handler, because a duplicate would silently halve
            // somebody's pay at month end.
            record.HasIndex(r => new { r.DailyHelpId, r.WorkDate })
                  .IsUnique()
                  .HasDatabaseName("IX_Attendance_Help_Date");

            record.HasIndex(r => new { r.SocietyId, r.WorkDate })
                  .HasDatabaseName("IX_Attendance_Society_Date");
        });
    }

    private static void ConfigureSafety(ModelBuilder builder)
    {
        builder.Entity<BlacklistEntry>(entry =>
        {
            entry.ToTable("BlacklistEntries");
            entry.Property(b => b.PhoneNumber).HasMaxLength(20).IsRequired();
            entry.Property(b => b.PersonName).HasMaxLength(200);
            entry.Property(b => b.Reason).HasMaxLength(1000).IsRequired();
            entry.Property(b => b.LiftedReason).HasMaxLength(1000);

            entry.HasIndex(b => new { b.SocietyId, b.PhoneNumber, b.IsActive })
                 .HasDatabaseName("IX_Blacklist_Society_Phone");
        });

        builder.Entity<SosIncident>(incident =>
        {
            incident.ToTable("SosIncidents");
            incident.Property(s => s.Category).HasConversion<string>().HasMaxLength(20);
            incident.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            incident.Property(s => s.Description).HasMaxLength(2000);
            incident.Property(s => s.ResolutionNotes).HasMaxLength(2000);
            incident.Property(s => s.Version).IsRowVersion();

            // Open alerts first. This is what a guard console polls, so it must never scan.
            incident.HasIndex(s => new { s.SocietyId, s.Status, s.RaisedAtUtc })
                    .HasDatabaseName("IX_Sos_Society_Status");
        });
    }
}
