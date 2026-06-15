using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Workflow.Data.ValueConverters;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

/// <summary>
/// Database context for workflow engine persistence.
/// Supports multi-schema architecture for multi-tenancy.
///
/// Schema isolation is achieved by injecting <see cref="ICurrentSchema"/> and passing the schema
/// name directly to entity table mappings in <see cref="OnModelCreating"/>. The compiled model is
/// cached per schema via <c>SchemaAwareModelCacheKeyFactory</c>, so no <c>SET search_path</c>
/// directive is ever sent — making this context safe under PgBouncer transaction-mode pooling.
/// </summary>
public class WorkflowDbContext : AetherDbContext<WorkflowDbContext>, IHasEfCoreBackgroundJobs
{
    private readonly ICurrentSchema? _currentSchema;

    /// <summary>
    /// Initializes a new instance of <see cref="WorkflowDbContext"/>.
    /// </summary>
    public WorkflowDbContext(
        DbContextOptions<WorkflowDbContext> options,
        ICurrentSchema? currentSchema = null,
        IDomainEventSink? eventSink = null)
        : base(options, eventSink)
    {
        _currentSchema = currentSchema;
    }

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

    /// <summary>
    /// Gets the current schema name as seen by this context instance.
    /// Exposed for <see cref="SchemaAwareModelCacheKeyFactory"/> so it can build
    /// the cache key without any DI dependency.
    /// </summary>
    public string? CurrentSchemaName => _currentSchema?.Name;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(null);

        base.OnModelCreating(builder);

        var schema = _currentSchema?.Name;
        builder.ConfigureWorkflow(schema);
        builder.ConfigureBackgroundJob(schema);
    }
}