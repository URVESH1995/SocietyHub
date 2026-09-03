using Microsoft.EntityFrameworkCore;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Scheduling.Api.Domain;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Scheduling.Api.Persistence;

/// <summary>
/// The Scheduling service's store. Society-scoped: a slot is on one society's service day and
/// a job is in one of its flats.
/// </summary>
public sealed class SchedulingDbContext : TenantDbContext
{
    public SchedulingDbContext(
        DbContextOptions<SchedulingDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<ServiceSlot> Slots => Set<ServiceSlot>();

    public DbSet<SlotTechnician> SlotTechnicians => Set<SlotTechnician>();

    public DbSet<ServiceJob> Jobs => Set<ServiceJob>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<ServiceSlot>(slot =>
        {
            slot.ToTable("service_slots");
            slot.HasKey(s => s.Id);
            slot.Property(s => s.Version).IsRowVersion();

            slot.HasIndex(s => new { s.DriveId, s.ServiceDate })
                .HasDatabaseName("ix_slots_drive_date");

            slot.HasMany(s => s.Technicians)
                .WithOne()
                .HasForeignKey(t => t.SlotId)
                .OnDelete(DeleteBehavior.Cascade);

            slot.Navigation(s => s.Technicians).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<SlotTechnician>(assignment =>
        {
            assignment.ToTable("slot_technicians");
            assignment.HasKey(t => t.Id);
            assignment.Property(t => t.TechnicianName).HasMaxLength(200).IsRequired();

            // One person cannot be on the same slot twice. Enforced here as well as in the
            // aggregate, because a retry racing itself is enough to defeat an in-memory check.
            assignment.HasIndex(t => new { t.SlotId, t.TechnicianId })
                      .IsUnique()
                      .HasDatabaseName("ux_slot_technician");
        });

        builder.Entity<ServiceJob>(job =>
        {
            job.ToTable("service_jobs");
            job.HasKey(j => j.Id);

            job.Property(j => j.Status).HasConversion<int>();
            job.Property(j => j.CompletionCode).HasMaxLength(8).IsRequired();
            job.Property(j => j.TechnicianName).HasMaxLength(200);
            job.Property(j => j.ProofPhotoKey).HasMaxLength(500);
            job.Property(j => j.TechnicianNotes).HasMaxLength(2000);
            job.Property(j => j.ResidentComment).HasMaxLength(2000);
            job.Property(j => j.CancellationReason).HasMaxLength(500);
            job.Property(j => j.Version).IsRowVersion();

            // One job per enrolment, in the schema. A duplicate would mean a vendor being paid
            // twice for one flat, which is exactly the kind of error that reconciles to a real
            // loss rather than a support ticket.
            job.HasIndex(j => j.EnrolmentId)
               .IsUnique()
               .HasDatabaseName("ux_jobs_enrolment");

            job.HasIndex(j => new { j.SlotId, j.Status }).HasDatabaseName("ix_jobs_slot");
            job.HasIndex(j => new { j.DriveId, j.Status }).HasDatabaseName("ix_jobs_drive");

            // The resident's "my jobs" screen, which is the most-hit query in this service.
            job.HasIndex(j => new { j.SocietyId, j.ResidentUserId, j.Status })
               .HasDatabaseName("ix_jobs_resident");
        });

        base.OnModelCreating(builder);
    }
}
