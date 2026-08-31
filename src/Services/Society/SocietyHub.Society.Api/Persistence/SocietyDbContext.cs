using Microsoft.EntityFrameworkCore;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.Society.Api.Domain;

namespace SocietyHub.Society.Api.Persistence;

/// <summary>
/// The Society service's store.
///
/// Unlike Identity, this inherits <see cref="TenantDbContext"/>, so every
/// <c>ITenantScoped</c> entity is discovered and filtered during model building with no
/// per-entity registration. Adding a table next month cannot quietly opt out of isolation —
/// the only way is to not implement the interface, which is a visible decision a reviewer
/// sees, and which the convention tests then fail on.
/// </summary>
public sealed class SocietyDbContext : TenantDbContext
{
    public SocietyDbContext(DbContextOptions<SocietyDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<Domain.Society> Societies => Set<Domain.Society>();

    public DbSet<Tower> Towers => Set<Tower>();

    public DbSet<Flat> Flats => Set<Flat>();

    public DbSet<Resident> Residents => Set<Resident>();

    public DbSet<Vehicle> Vehicles => Set<Vehicle>();

    public DbSet<ParkingSlot> ParkingSlots => Set<ParkingSlot>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        ConfigureSocieties(builder);
        ConfigureTowers(builder);
        ConfigureFlats(builder);
        ConfigureResidents(builder);
        ConfigureVehicles(builder);
        ConfigureParkingSlots(builder);

        // Applies the tenant filters last, after every entity is known to the model.
        // The society is its own tenant: its SocietyId is a computed `=> Id`, which EF cannot

        // map, so the convention in TenantDbContext cannot build a filter for it. Declared here

        // on Id instead, before base runs, which then sees it and leaves it alone.

        builder.Entity<Domain.Society>().Ignore(s => s.SocietyId);

        builder.Entity<Domain.Society>()

               .HasQueryFilter(TenantFilterName, s => s.Id == ActiveSocietyId);


        base.OnModelCreating(builder);
    }

    private static void ConfigureSocieties(ModelBuilder builder)
    {
        builder.Entity<Domain.Society>(society =>
        {
            society.ToTable("Societies");
            society.HasKey(s => s.Id);

            // SocietyId is the entity's own Id, so it is computed rather than stored. Telling
            // EF to ignore it is what lets the inherited filter still compare against it.
            society.Ignore(s => s.SocietyId);

            society.Property(s => s.Name).HasMaxLength(200).IsRequired();
            society.Property(s => s.RegistrationNumber).HasMaxLength(100);
            society.Property(s => s.AddressLine1).HasMaxLength(200);
            society.Property(s => s.AddressLine2).HasMaxLength(200);
            society.Property(s => s.City).HasMaxLength(100);
            society.Property(s => s.State).HasMaxLength(100);
            society.Property(s => s.PostalCode).HasMaxLength(20);

            society.Property(s => s.Version).IsRowVersion();

            // Settings are part of the society, never separately addressable, so they live in
            // the same row rather than in a table nothing else joins to.
            society.OwnsOne(s => s.Settings, settings =>
            {
                settings.Property(x => x.DefaultLanguage).HasMaxLength(16).IsRequired();
                settings.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
                settings.Property(x => x.Currency).HasMaxLength(3).IsRequired();
                settings.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
            });

            society.HasMany(s => s.Towers)
                   .WithOne()
                   .HasForeignKey(t => t.SocietyId)
                   .OnDelete(DeleteBehavior.Cascade);

            society.Navigation(s => s.Towers).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    private static void ConfigureTowers(ModelBuilder builder)
    {
        builder.Entity<Tower>(tower =>
        {
            tower.ToTable("Towers");
            tower.HasKey(t => t.Id);
            tower.Property(t => t.Name).HasMaxLength(100).IsRequired();

            tower.HasIndex(t => new { t.SocietyId, t.Name })
                 .IsUnique()
                 .HasDatabaseName("IX_Towers_Society_Name");

            tower.HasMany(t => t.Flats)
                 .WithOne(f => f.Tower!)
                 .HasForeignKey(f => f.TowerId)
                 .OnDelete(DeleteBehavior.Cascade);

            tower.Navigation(t => t.Flats).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    private static void ConfigureFlats(ModelBuilder builder)
    {
        builder.Entity<Flat>(flat =>
        {
            flat.ToTable("Flats");
            flat.HasKey(f => f.Id);
            flat.Property(f => f.FlatNumber).HasMaxLength(32).IsRequired();
            flat.Property(f => f.FlatType).HasMaxLength(32).IsRequired();
            flat.Property(f => f.CarpetAreaSqFt).HasPrecision(10, 2);
            flat.Property(f => f.Occupancy).HasConversion<string>().HasMaxLength(20);
            flat.Property(f => f.Version).IsRowVersion();

            // Unique within a tower, not the society: two towers may both have an A-101.
            flat.HasIndex(f => new { f.TowerId, f.FlatNumber })
                .IsUnique()
                .HasDatabaseName("IX_Flats_Tower_Number");

            // Gate lookups resolve a flat by number within a society on every visitor entry.
            flat.HasIndex(f => new { f.SocietyId, f.FlatNumber })
                .HasDatabaseName("IX_Flats_Society_Number");

            flat.HasMany(f => f.Residents)
                .WithOne(r => r.Flat!)
                .HasForeignKey(r => r.FlatId)
                .OnDelete(DeleteBehavior.Cascade);

            flat.Navigation(f => f.Residents).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    private static void ConfigureResidents(ModelBuilder builder)
    {
        builder.Entity<Resident>(resident =>
        {
            resident.ToTable("Residents");
            resident.HasKey(r => r.Id);
            resident.Property(r => r.Relationship).HasConversion<string>().HasMaxLength(20);
            resident.Property(r => r.DirectoryVisibility).HasConversion<string>().HasMaxLength(20);

            // "Which flats does this user live in" runs on every gate approval.
            resident.HasIndex(r => new { r.SocietyId, r.UserId })
                    .HasDatabaseName("IX_Residents_Society_User");

            resident.HasIndex(r => r.FlatId).HasDatabaseName("IX_Residents_Flat");
        });
    }

    private static void ConfigureVehicles(ModelBuilder builder)
    {
        builder.Entity<Vehicle>(vehicle =>
        {
            vehicle.ToTable("Vehicles");
            vehicle.HasKey(v => v.Id);
            vehicle.Property(v => v.RegistrationNumber).HasMaxLength(20).IsRequired();
            vehicle.Property(v => v.Type).HasConversion<string>().HasMaxLength(20);
            vehicle.Property(v => v.Make).HasMaxLength(60);
            vehicle.Property(v => v.Model).HasMaxLength(60);
            vehicle.Property(v => v.Colour).HasMaxLength(40);

            // One plate per society. ANPR matches against this on every gate entry in Phase 3,
            // and a duplicate would make "whose car is this" ambiguous at the barrier.
            vehicle.HasIndex(v => new { v.SocietyId, v.RegistrationNumber })
                   .IsUnique()
                   .HasDatabaseName("IX_Vehicles_Society_Registration");
        });
    }

    private static void ConfigureParkingSlots(ModelBuilder builder)
    {
        builder.Entity<ParkingSlot>(slot =>
        {
            slot.ToTable("ParkingSlots");
            slot.HasKey(p => p.Id);
            slot.Property(p => p.SlotNumber).HasMaxLength(32).IsRequired();
            slot.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
            slot.Property(p => p.Level).HasMaxLength(20);

            slot.HasIndex(p => new { p.SocietyId, p.SlotNumber })
                .IsUnique()
                .HasDatabaseName("IX_ParkingSlots_Society_Number");
        });
    }
}
