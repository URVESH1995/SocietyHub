using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Identity.Api.Persistence;

/// <summary>
/// The Identity service's store.
///
/// Inherits ASP.NET Identity's context rather than <see cref="TenantDbContext"/>, because a
/// class can only have one base and Identity's brings the whole user, role and claim schema
/// with it. The tenant filters are therefore applied by hand below — which is workable
/// precisely because almost nothing here is society-scoped.
///
/// That is the shape of identity in a multi-tenant system: a <b>person</b> is global and
/// their <b>standing in a society</b> is scoped. Users, roles and OTP challenges are keyed on
/// a phone number that exists before any society is known. Memberships and guard devices are
/// the tenant-scoped part, and they are the two with filters.
/// </summary>
public sealed class SocietyHubIdentityDbContext
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    private readonly ITenantContext _tenantContext;

    public SocietyHubIdentityDbContext(
        DbContextOptions<SocietyHubIdentityDbContext> options,
        ITenantContext tenantContext)
        : base(options) => _tenantContext = tenantContext;

    public DbSet<SocietyMembership> SocietyMemberships => Set<SocietyMembership>();

    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<GuardDevice> GuardDevices => Set<GuardDevice>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>
    /// Read by the compiled query filters on every query, so it reflects the current request.
    /// Falls back to <see cref="Guid.Empty"/>, which matches no row: default deny.
    /// </summary>
    public Guid ActiveSocietyId => _tenantContext.SocietyId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        ConfigureUsers(builder);
        ConfigureMemberships(builder);
        ConfigureOtpChallenges(builder);
        ConfigureRefreshTokens(builder);
        ConfigureGuardDevices(builder);

    }

    private static void ConfigureUsers(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            user.Property(u => u.PreferredLanguage).HasMaxLength(16);

            // Phone is the sign-in identity, so it must be unique platform-wide and present.
            // The filtered index tolerates the handful of accounts created by an administrator
            // before a phone number is known.
            user.HasIndex(u => u.PhoneNumber)
                .IsUnique()
                .HasFilter("[PhoneNumber] IS NOT NULL")
                .HasDatabaseName("IX_Users_PhoneNumber");
        });
    }

    private void ConfigureMemberships(ModelBuilder builder)
    {
        builder.Entity<SocietyMembership>(membership =>
        {
            membership.ToTable("SocietyMemberships");
            membership.HasKey(m => m.Id);
            membership.Property(m => m.Role).HasMaxLength(64).IsRequired();
            membership.Property(m => m.Relationship).HasMaxLength(32);

            membership
                .HasOne(m => m.User)
                .WithMany(u => u.Memberships)
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // One role per person per society. A committee member who is also a resident holds
            // the higher role, not two rows — otherwise "what may they do here" has two answers.
            membership
                .HasIndex(m => new { m.SocietyId, m.UserId })
                .IsUnique()
                .HasDatabaseName("IX_Memberships_Society_User");

            // Layer 1, applied by hand because this context cannot inherit TenantDbContext.
            // Referencing ActiveSocietyId on `this` is what makes EF re-evaluate it per query
            // rather than baking in whichever context first built the model.
            membership.HasQueryFilter(
                TenantDbContext.TenantFilterName,
                m => m.SocietyId == ActiveSocietyId);
        });
    }

    private static void ConfigureOtpChallenges(ModelBuilder builder)
    {
        builder.Entity<OtpChallenge>(otp =>
        {
            otp.ToTable("OtpChallenges");
            otp.HasKey(o => o.Id);
            otp.Property(o => o.PhoneNumber).HasMaxLength(20).IsRequired();
            otp.Property(o => o.CodeHash).HasMaxLength(64).IsRequired();
            otp.Property(o => o.Salt).HasMaxLength(32).IsRequired();
            otp.Property(o => o.RequestedFromIp).HasMaxLength(64);

            // Finds the newest live challenge for a phone, which is the only query made.
            otp.HasIndex(o => new { o.PhoneNumber, o.ExpiresAtUtc })
               .HasDatabaseName("IX_Otp_Phone_Expiry");
        });
    }

    private static void ConfigureRefreshTokens(ModelBuilder builder)
    {
        builder.Entity<RefreshToken>(token =>
        {
            token.ToTable("RefreshTokens");
            token.HasKey(t => t.Id);
            token.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
            token.Property(t => t.RevocationReason).HasMaxLength(200);
            token.Property(t => t.CreatedFromIp).HasMaxLength(64);
            token.Property(t => t.UserAgent).HasMaxLength(400);

            // Every presented token is looked up by hash, so this carries the hot path.
            token.HasIndex(t => t.TokenHash)
                 .IsUnique()
                 .HasDatabaseName("IX_RefreshTokens_Hash");

            // Revoking a family on reuse detection sweeps by this.
            token.HasIndex(t => t.FamilyId).HasDatabaseName("IX_RefreshTokens_Family");
        });
    }

    private void ConfigureGuardDevices(ModelBuilder builder)
    {
        builder.Entity<GuardDevice>(device =>
        {
            device.ToTable("GuardDevices");
            device.HasKey(d => d.Id);
            device.Property(d => d.DeviceIdentifier).HasMaxLength(200).IsRequired();
            device.Property(d => d.DisplayName).HasMaxLength(200).IsRequired();
            device.Property(d => d.PinHash).HasMaxLength(64);
            device.Property(d => d.PinSalt).HasMaxLength(32);

            device.HasIndex(d => new { d.SocietyId, d.DeviceIdentifier })
                  .IsUnique()
                  .HasDatabaseName("IX_GuardDevices_Society_Device");

            device.HasQueryFilter(
                TenantDbContext.TenantFilterName,
                d => d.SocietyId == ActiveSocietyId);
        });
    }
}
