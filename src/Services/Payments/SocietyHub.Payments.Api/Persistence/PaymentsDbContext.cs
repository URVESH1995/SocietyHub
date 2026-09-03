using Microsoft.EntityFrameworkCore;
using SocietyHub.Payments.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Payments.Api.Persistence;

/// <summary>
/// The Payments service's store.
///
/// Society-scoped: a payment belongs to a resident of one society, and a reconciliation report
/// that leaked another society's totals would be the worst possible tenancy failure.
/// </summary>
public sealed class PaymentsDbContext : TenantDbContext
{
    public PaymentsDbContext(
        DbContextOptions<PaymentsDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<PaymentOrder> Orders => Set<PaymentOrder>();

    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<PaymentOrder>(order =>
        {
            order.ToTable("payment_orders");
            order.HasKey(o => o.Id);

            order.Property(o => o.Purpose).HasMaxLength(50).IsRequired();
            order.Property(o => o.GatewayOrderId).HasMaxLength(100);
            order.Property(o => o.GatewayPaymentId).HasMaxLength(100);
            order.Property(o => o.FailureReason).HasMaxLength(500);
            order.Property(o => o.Status).HasConversion<int>();
            order.Property(o => o.Version).IsRowVersion();

            // One order per thing being paid for. This unique index is what makes creation
            // idempotent under a double tap, and a duplicate here is a resident charged twice.
            order.HasIndex(o => new { o.Purpose, o.ReferenceId })
                 .IsUnique()
                 .HasDatabaseName("ux_orders_reference");

            // The webhook's only lookup, on every delivery from the gateway.
            order.HasIndex(o => o.GatewayOrderId)
                 .HasDatabaseName("ix_orders_gateway");

            order.HasMany(o => o.Ledger)
                 .WithOne()
                 .HasForeignKey(e => e.OrderId)

                 // Restrict, not Cascade. A ledger is the record of money that moved; deleting
                 // an order must not be able to erase it, and if something tries, it should
                 // fail loudly rather than quietly succeed.
                 .OnDelete(DeleteBehavior.Restrict);

            order.Navigation(o => o.Ledger).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<LedgerEntry>(entry =>
        {
            entry.ToTable("ledger_entries");
            entry.HasKey(e => e.Id);

            entry.Property(e => e.Kind).HasConversion<int>();
            entry.Property(e => e.GatewayReference).HasMaxLength(100).IsRequired();
            entry.Property(e => e.Reason).HasMaxLength(200);

            // One entry per gateway reference per kind. The database-level guarantee behind
            // "a refund is issued exactly once", and the last line of defence if both the
            // inbox and the gateway's own idempotency were somehow bypassed.
            entry.HasIndex(e => new { e.Kind, e.GatewayReference })
                 .IsUnique()
                 .HasDatabaseName("ux_ledger_reference");

            entry.HasIndex(e => e.OrderId).HasDatabaseName("ix_ledger_order");
        });

        base.OnModelCreating(builder);
    }
}
