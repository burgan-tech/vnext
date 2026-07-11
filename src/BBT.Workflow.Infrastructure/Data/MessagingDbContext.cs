using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Persistence;
using BBT.Workflow.Infrastructure.Execution.Locks;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

public class MessagingDbContext(
    DbContextOptions<MessagingDbContext> options)
    : AetherDbContext<MessagingDbContext>(options),
        IHasEfCoreInbox, IHasEfCoreOutbox, IHasEfCoreBackgroundJobs
{
    /// <summary>
    /// Gets or sets the inbox messages
    /// </summary>
    public virtual DbSet<InboxMessage> InboxMessages { get; set; }

    /// <summary>
    /// Gets or sets the outbox messages
    /// </summary>
    public virtual DbSet<OutboxMessage> OutboxMessages { get; set; }

    /// <summary>
    /// Background job tracking records. Primary store as of this migration.
    /// WorkflowDbContext.BackgroundJobs is kept for backwards-compat only (marked Obsolete).
    /// </summary>
    public virtual DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }

    /// <summary>
    /// Platform-owned distributed lock leases (see <see cref="NpgsqlDistributedLockService"/>).
    /// Schema-owned here so the table is created via EF Core migration at deploy time; the
    /// lock service performs runtime operations with raw ADO.NET on dedicated connections.
    /// </summary>
    public virtual DbSet<DistributedLockRecord> DistributedLocks { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("sys_queues");
        base.OnModelCreating(builder);

        builder.ConfigureInbox();
        builder.ConfigureOutbox();
        builder.ConfigureBackgroundJob();

        builder.Entity<DistributedLockRecord>(b =>
        {
            b.ToTable("DistributedLocks");
            b.HasKey(l => l.Key);
            b.Property(l => l.Key).HasMaxLength(512);
            b.Property(l => l.Owner).HasMaxLength(256).IsRequired();
            b.Property(l => l.Fence).IsRequired();
            b.Property(l => l.ExpiresAt).IsRequired();
        });
    }
}