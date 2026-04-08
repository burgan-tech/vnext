using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.Persistence;
using BBT.Workflow.Data.ValueConverters;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

/// <summary>
/// Database context for workflow engine persistence.
/// Supports multi-schema architecture for multi-tenancy.
/// </summary>
public class WorkflowDbContext(
    DbContextOptions<WorkflowDbContext> options,
    IDomainEventSink? eventSink = null)
    : AetherDbContext<WorkflowDbContext>(options, eventSink),
        IHasEfCoreBackgroundJobs
{
    /// <summary>
    /// Gets or sets the workflow instances.
    /// </summary>
    public virtual DbSet<Instance> Instances { get; set; }

    /// <summary>
    /// Gets or sets the instance correlations for subflow relationships.
    /// </summary>
    public virtual DbSet<InstanceCorrelation> InstanceCorrelations { get; set; }

    /// <summary>
    /// Gets or sets the instance data versions.
    /// </summary>
    public virtual DbSet<InstanceData> InstancesData { get; set; }

    /// <summary>
    /// Gets or sets the instance actions (deprecated).
    /// </summary>
    public virtual DbSet<InstanceAction> InstanceActions { get; set; }

    /// <summary>
    /// Gets or sets the instance task execution records.
    /// </summary>
    public virtual DbSet<InstanceTask> InstanceTasks { get; set; }

    /// <summary>
    /// Gets or sets the instance transition execution records.
    /// </summary>
    public virtual DbSet<InstanceTransition> InstanceTransitions { get; set; }

    /// <summary>
    /// Gets or sets the instance background jobs.
    /// </summary>
    public virtual DbSet<InstanceJob> InstanceJobs { get; set; }

    /// <summary>
    /// Gets or sets the background jobs
    /// </summary>
    public virtual DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }

    protected override void ConfigureConventions(ModelConfigurationBuilder cfg)
    {
        cfg.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        cfg.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(null);

        base.OnModelCreating(builder);

        builder.ConfigureWorkflow();
        builder.ConfigureBackgroundJob();
    }
}