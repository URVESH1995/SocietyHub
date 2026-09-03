using Microsoft.EntityFrameworkCore;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.Vendor.Api.Domain;

namespace SocietyHub.Vendor.Api.Persistence;

/// <summary>
/// The Vendor service's store.
///
/// <para>
/// <b>A plain DbContext, not a TenantDbContext, and that is the point of this service.</b>
/// Vendors are platform data: one company serves many societies, which is the only reason a
/// bulk discount exists. A tenant filter here would give every society its own private copy of
/// the same plumber.
/// </para>
///
/// <para>
/// Because the automatic filter is absent, nothing in this service accidentally inherits
/// protection. Access is authorisation instead: writes require the platform policy, and a
/// society reads vendors through a query that filters by service area — never by tenancy.
/// A test asserts no entity here implements <c>ITenantScoped</c>, so a future aggregate that
/// genuinely is society-scoped cannot be added to this context by mistake.
/// </para>
/// </summary>
public sealed class VendorDbContext : DbContext
{
    public VendorDbContext(DbContextOptions<VendorDbContext> options)
        : base(options)
    {
    }

    public DbSet<Domain.Vendor> Vendors => Set<Domain.Vendor>();

    public DbSet<VendorDocument> VendorDocuments => Set<VendorDocument>();

    public DbSet<ServiceArea> ServiceAreas => Set<ServiceArea>();

    public DbSet<RateCard> RateCards => Set<RateCard>();

    public DbSet<PriceSlab> PriceSlabs => Set<PriceSlab>();

    public DbSet<Technician> Technicians => Set<Technician>();

    public DbSet<VendorPerformance> VendorPerformance => Set<VendorPerformance>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<Domain.Vendor>(vendor =>
        {
            vendor.ToTable("vendors");
            vendor.HasKey(v => v.Id);

            vendor.Property(v => v.LegalName).HasMaxLength(300).IsRequired();
            vendor.Property(v => v.TradingName).HasMaxLength(300).IsRequired();
            vendor.Property(v => v.ContactPhone).HasMaxLength(20).IsRequired();
            vendor.Property(v => v.ContactEmail).HasMaxLength(320);
            vendor.Property(v => v.GstNumber).HasMaxLength(15);
            vendor.Property(v => v.PanNumber).HasMaxLength(10);
            vendor.Property(v => v.StatusReason).HasMaxLength(1000);
            vendor.Property(v => v.Status).HasConversion<int>();
            vendor.Property(v => v.Version).IsRowVersion();

            // One company, once. A duplicate GSTIN is either a data-entry mistake or somebody
            // re-registering to escape a suspension, and both are worth blocking at the
            // database rather than in a service that can be bypassed by the next one.
            vendor.HasIndex(v => v.GstNumber)
                  .IsUnique()
                  .HasFilter("[GstNumber] IS NOT NULL")
                  .HasDatabaseName("ux_vendors_gstin");

            vendor.HasIndex(v => v.Status).HasDatabaseName("ix_vendors_status");

            vendor.HasMany(v => v.Documents)
                  .WithOne()
                  .HasForeignKey(d => d.VendorId)
                  .OnDelete(DeleteBehavior.Cascade);

            vendor.HasMany(v => v.ServiceAreas)
                  .WithOne()
                  .HasForeignKey(a => a.VendorId)
                  .OnDelete(DeleteBehavior.Cascade);

            vendor.Navigation(v => v.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
            vendor.Navigation(v => v.ServiceAreas).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<VendorDocument>(document =>
        {
            document.ToTable("vendor_documents");
            document.HasKey(d => d.Id);
            document.Property(d => d.Kind).HasConversion<int>();
            document.Property(d => d.StorageKey).HasMaxLength(500).IsRequired();
        });

        builder.Entity<ServiceArea>(area =>
        {
            area.ToTable("vendor_service_areas");
            area.HasKey(a => a.Id);
            area.Property(a => a.City).HasMaxLength(120).IsRequired();
            area.Property(a => a.PostalCode).HasMaxLength(12).IsRequired();

            // Serves the only question a society asks: who will come to my postal code.
            area.HasIndex(a => a.PostalCode).HasDatabaseName("ix_service_areas_postal");
        });

        builder.Entity<RateCard>(card =>
        {
            card.ToTable("rate_cards");
            card.HasKey(c => c.Id);
            card.Property(c => c.ServiceCode).HasMaxLength(100).IsRequired();
            card.Property(c => c.UnitLabel).HasMaxLength(100).IsRequired();
            card.Property(c => c.Version).IsRowVersion();

            // One published card per vendor per service. Two would make the price a question
            // of which row a query happened to return first.
            card.HasIndex(c => new { c.VendorId, c.ServiceCode })
                .IsUnique()
                .HasDatabaseName("ux_rate_cards_vendor_service");

            card.HasMany(c => c.Slabs)
                .WithOne()
                .HasForeignKey(s => s.RateCardId)
                .OnDelete(DeleteBehavior.Cascade);

            card.Navigation(c => c.Slabs).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<PriceSlab>(slab =>
        {
            slab.ToTable("price_slabs");
            slab.HasKey(s => s.Id);

            // bigint paise. See RateCard's remarks — a decimal here is a reconciliation
            // failure waiting for a drive large enough to expose it.
            slab.Property(s => s.UnitPricePaise);
        });

        builder.Entity<Technician>(technician =>
        {
            technician.ToTable("technicians");
            technician.HasKey(t => t.Id);
            technician.Property(t => t.FullName).HasMaxLength(200).IsRequired();
            technician.Property(t => t.Phone).HasMaxLength(20).IsRequired();
            technician.Property(t => t.Version).IsRowVersion();

            technician.HasIndex(t => new { t.VendorId, t.IsActive })
                      .HasDatabaseName("ix_technicians_vendor");
        });

        builder.Entity<VendorPerformance>(performance =>
        {
            performance.ToTable("vendor_performance");
            performance.HasKey(p => p.Id);

            performance.HasIndex(p => p.VendorId)
                       .IsUnique()
                       .HasDatabaseName("ux_performance_vendor");
        });

        base.OnModelCreating(builder);
    }
}
