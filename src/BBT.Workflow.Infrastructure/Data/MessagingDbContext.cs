using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Persistence;
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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("sys_queues");
        base.OnModelCreating(builder);

        builder.ConfigureInbox();
        builder.ConfigureOutbox();
        builder.ConfigureBackgroundJob();
    }
}