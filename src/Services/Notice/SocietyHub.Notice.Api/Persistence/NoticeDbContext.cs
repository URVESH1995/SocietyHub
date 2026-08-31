using Microsoft.EntityFrameworkCore;
using SocietyHub.Notice.Api.Domain;
using SocietyHub.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Notice.Api.Persistence;

/// <summary>
/// The Notice service's store.
///
/// Read-heavy and small: a society publishes a few notices a week and every resident opens the
/// board daily, so the indexes serve the feed query and nothing else pays for them.
/// </summary>
public sealed class NoticeDbContext : TenantDbContext
{
    public NoticeDbContext(DbContextOptions<NoticeDbContext> options, ITenantContext tenantContext)
        : base(options, tenantContext)
    {
    }

    public DbSet<Domain.Notice> Notices => Set<Domain.Notice>();

    public DbSet<NoticeAcknowledgement> NoticeAcknowledgements => Set<NoticeAcknowledgement>();

    public DbSet<Poll> Polls => Set<Poll>();

    public DbSet<PollOption> PollOptions => Set<PollOption>();

    public DbSet<PollVote> PollVotes => Set<PollVote>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
        builder.ApplyConfiguration(new InboxMessageConfiguration());

        builder.Entity<Domain.Notice>(notice =>
        {
            notice.ToTable("notices");
            notice.HasKey(n => n.Id);

            notice.Property(n => n.AuthorName).HasMaxLength(200).IsRequired();
            notice.Property(n => n.TitleEn).HasMaxLength(300).IsRequired();
            notice.Property(n => n.BodyEn).HasMaxLength(8000).IsRequired();
            notice.Property(n => n.TitleHi).HasMaxLength(300);
            notice.Property(n => n.BodyHi).HasMaxLength(8000);
            notice.Property(n => n.TargetTowers).HasMaxLength(1000);
            notice.Property(n => n.TargetFlatIds).HasMaxLength(4000);
            notice.Property(n => n.Category).HasConversion<int>();
            notice.Property(n => n.Status).HasConversion<int>();
            notice.Property(n => n.Audience).HasConversion<int>();
            notice.Property(n => n.Version).IsRowVersion();

            // The feed query, and the only one that runs often enough to matter: this society's
            // published notices, pinned first, newest first.
            notice.HasIndex(n => new { n.SocietyId, n.Status, n.IsPinned, n.PublishedAtUtc })
                .HasDatabaseName("ix_notices_feed");

            // Serves the expiry sweep.
            notice.HasIndex(n => new { n.Status, n.ExpiresAtUtc })
                .HasDatabaseName("ix_notices_expiry");

            notice.HasMany(n => n.Acknowledgements)
                .WithOne()
                .HasForeignKey(a => a.NoticeId)
                .OnDelete(DeleteBehavior.Cascade);

            notice.Navigation(n => n.Acknowledgements).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<NoticeAcknowledgement>(ack =>
        {
            ack.ToTable("notice_acknowledgements");
            ack.HasKey(a => a.Id);

            // One acknowledgement per person per notice, enforced by the database rather than
            // only by the aggregate — two tabs are enough to defeat an in-memory check.
            ack.HasIndex(a => new { a.NoticeId, a.UserId })
                .IsUnique()
                .HasDatabaseName("ux_notice_ack_user");
        });

        builder.Entity<Poll>(poll =>
        {
            poll.ToTable("polls");
            poll.HasKey(p => p.Id);

            poll.Property(p => p.QuestionEn).HasMaxLength(500).IsRequired();
            poll.Property(p => p.QuestionHi).HasMaxLength(500);
            poll.Property(p => p.Kind).HasConversion<int>();
            poll.Property(p => p.Status).HasConversion<int>();
            poll.Property(p => p.Version).IsRowVersion();

            poll.HasIndex(p => new { p.SocietyId, p.Status, p.ClosesAtUtc })
                .HasDatabaseName("ix_polls_open");

            poll.HasMany(p => p.Options)
                .WithOne()
                .HasForeignKey(o => o.PollId)
                .OnDelete(DeleteBehavior.Cascade);

            poll.HasMany(p => p.Votes)
                .WithOne()
                .HasForeignKey(v => v.PollId)
                .OnDelete(DeleteBehavior.Cascade);

            poll.Navigation(p => p.Options).UsePropertyAccessMode(PropertyAccessMode.Field);
            poll.Navigation(p => p.Votes).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<PollOption>(option =>
        {
            option.ToTable("poll_options");
            option.HasKey(o => o.Id);
            option.Property(o => o.LabelEn).HasMaxLength(200).IsRequired();
            option.Property(o => o.LabelHi).HasMaxLength(200);
        });

        builder.Entity<PollVote>(vote =>
        {
            vote.ToTable("poll_votes");
            vote.HasKey(v => v.Id);

            // The rule that makes the vote count defensible: one per flat, in the schema.
            // A society challenging a resolution will look at exactly this.
            vote.HasIndex(v => new { v.PollId, v.FlatId })
                .IsUnique()
                .HasDatabaseName("ux_poll_vote_flat");
        });

        base.OnModelCreating(builder);
    }
}
