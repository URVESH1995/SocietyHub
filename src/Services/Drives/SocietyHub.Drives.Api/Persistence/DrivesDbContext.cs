using Microsoft.EntityFrameworkCore;
using SocietyHub.Drives.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Drives.Api.Persistence;

/// <summary>
/// The Drives service's store.
///
/// Society-scoped again, unlike Vendor. A drive belongs to one society — it is that society's
/// residents pooling their money — even though the vendor it buys from is shared. The
/// catalogue is the one exception here and is explicitly excluded from the filter.
/// </summary>
public sealed class DrivesDbContext : TenantDbContext
{
    public DrivesDbContext(DbContextOptions<DrivesDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<ServiceDrive> Drives => Set<ServiceDrive>();

    public DbSet<DriveEnrolment> Enrolments => Set<DriveEnrolment>();

    public DbSet<ServiceCatalogueItem> Catalogue => Set<ServiceCatalogueItem>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<ServiceDrive>(drive =>
        {
            drive.ToTable("service_drives");
            drive.HasKey(d => d.Id);

            drive.Property(d => d.ServiceCode).HasMaxLength(100).IsRequired();
            drive.Property(d => d.CancellationReason).HasMaxLength(500);
            drive.Property(d => d.Status).HasConversion<int>();
            drive.Property(d => d.Version).IsRowVersion();

            // The lifecycle worker's query: open drives past their cut-off, and drives still
            // refunding. Both run every minute across every tenant, so they are the only
            // queries here that warrant an index of their own.
            drive.HasIndex(d => new { d.Status, d.CutOffAtUtc })
                 .HasDatabaseName("ix_drives_lifecycle");

            drive.HasIndex(d => new { d.SocietyId, d.Status })
                 .HasDatabaseName("ix_drives_society");

            drive.HasMany(d => d.Enrolments)
                 .WithOne()
                 .HasForeignKey(e => e.DriveId)
                 .OnDelete(DeleteBehavior.Cascade);

            drive.Navigation(d => d.Enrolments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<DriveEnrolment>(enrolment =>
        {
            enrolment.ToTable("drive_enrolments");
            enrolment.HasKey(e => e.Id);

            enrolment.Property(e => e.PaymentReference).HasMaxLength(100);
            enrolment.Property(e => e.RefundReference).HasMaxLength(100);
            enrolment.Property(e => e.Status).HasConversion<int>();

            // One live enrolment per flat per drive, in the schema and not only in the
            // aggregate. Two tabs are enough to defeat an in-memory check, and the cost of
            // losing that race is a household charged twice for one service.
            enrolment.HasIndex(e => new { e.DriveId, e.FlatId })
                     .IsUnique()
                     .HasFilter("[Status] IN (0, 1)")
                     .HasDatabaseName("ux_enrolment_flat_per_drive");

            enrolment.HasIndex(e => new { e.DriveId, e.Status })
                     .HasDatabaseName("ix_enrolments_refund_sweep");
        });

        builder.Entity<ServiceCatalogueItem>(item =>
        {
            item.ToTable("service_catalogue");
            item.HasKey(i => i.Id);

            item.Property(i => i.Code).HasMaxLength(100).IsRequired();
            item.Property(i => i.NameEn).HasMaxLength(200).IsRequired();
            item.Property(i => i.NameHi).HasMaxLength(200).IsRequired();
            item.Property(i => i.UnitLabelEn).HasMaxLength(100).IsRequired();
            item.Property(i => i.UnitLabelHi).HasMaxLength(100).IsRequired();
            item.Property(i => i.Category).HasConversion<int>();

            item.HasIndex(i => i.Code).IsUnique().HasDatabaseName("ux_catalogue_code");
        });

        base.OnModelCreating(builder);
    }
}
