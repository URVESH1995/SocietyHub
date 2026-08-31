using Microsoft.EntityFrameworkCore;
using SocietyHub.Notification.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Notification.Api.Persistence;

/// <summary>
/// The Notification service's store.
///
/// Highest row count after the gate log — one delivery row per recipient per channel, so a
/// single notice to a 250-flat society writes 600 of them. The indexes serve exactly two
/// queries: the dispatcher finding what is due, and a resident reading their own inbox.
///
/// Templates are deliberately <em>not</em> tenant-scoped. They are platform content shared by
/// every society, and a per-society copy of forty templates in two languages would mean 170
/// societies each maintaining their own translations.
/// </summary>
public sealed class NotificationDbContext : TenantDbContext
{
    public NotificationDbContext(
        DbContextOptions<NotificationDbContext> options,
        ITenantContext tenantContext) : base(options, tenantContext)
    {
    }

    public DbSet<NotificationTemplate> Templates => Set<NotificationTemplate>();

    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();

    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<NotificationTemplate>(template =>
        {
            template.ToTable("NotificationTemplates");
            template.Property(t => t.EventKey).HasMaxLength(120).IsRequired();
            template.Property(t => t.Language).HasMaxLength(16).IsRequired();
            template.Property(t => t.Subject).HasMaxLength(300);
            template.Property(t => t.Body).HasMaxLength(2000).IsRequired();
            template.Property(t => t.Channel).HasConversion<string>().HasMaxLength(20);

            // Exactly one template per event, language and channel. A duplicate would make
            // which wording a resident receives depend on row order.
            template.HasIndex(t => new { t.EventKey, t.Language, t.Channel })
                    .IsUnique()
                    .HasDatabaseName("IX_Templates_Event_Language_Channel");
        });

        builder.Entity<NotificationDelivery>(delivery =>
        {
            delivery.ToTable("NotificationDeliveries");
            delivery.Property(d => d.EventKey).HasMaxLength(120).IsRequired();
            delivery.Property(d => d.Language).HasMaxLength(16).IsRequired();
            delivery.Property(d => d.Subject).HasMaxLength(300);
            delivery.Property(d => d.Body).HasMaxLength(2000).IsRequired();
            delivery.Property(d => d.Destination).HasMaxLength(400);
            delivery.Property(d => d.LastError).HasMaxLength(1000);
            delivery.Property(d => d.ProviderMessageId).HasMaxLength(200);

            delivery.Property(d => d.Channel).HasConversion<string>().HasMaxLength(20);
            delivery.Property(d => d.Urgency).HasConversion<string>().HasMaxLength(20);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);

            // The dispatcher's query. Status leads because pending rows are a small and
            // shrinking fraction of a table that only grows.
            delivery.HasIndex(d => new { d.Status, d.NextAttemptAtUtc })
                    .HasDatabaseName("IX_Deliveries_Due");

            // A resident's inbox.
            delivery.HasIndex(d => new { d.RecipientUserId, d.CreatedAtUtc })
                    .HasDatabaseName("IX_Deliveries_Recipient");
        });

        builder.Entity<NotificationPreference>(preference =>
        {
            preference.ToTable("NotificationPreferences");
            preference.Property(p => p.MutedEventKeys).HasMaxLength(2000);
            preference.Property(p => p.PushToken).HasMaxLength(400);

            preference.HasIndex(p => new { p.SocietyId, p.UserId })
                      .IsUnique()
                      .HasDatabaseName("IX_Preferences_Society_User");
        });

        base.OnModelCreating(builder);
    }
}
