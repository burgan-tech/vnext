using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data.ValueConverters;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
/// <remarks>
/// <para>
/// <b>DEPRECATED:</b> <see cref="BackgroundJobInfo"/> / <see cref="IHasEfCoreBackgroundJobs"/>
/// implementasyonu bu context'ten kaldırılacak. Birincil store <c>MessagingDbContext</c>'tir
/// (sys_queues.BackgroundJobs). Bu DbSet ve interface yalnızca geriye dönük uyum için tutulmaktadır.
/// </para>
/// </remarks>
public class WorkflowDbContext : AetherDbContext<WorkflowDbContext>, IHasEfCoreBackgroundJobs
{
    private readonly ICurrentSchema? _currentSchema;
    private readonly InstanceDataWriteOptions _instanceDataWriteOptions;
    private readonly ILogger _instanceDataWriteLogger;

    /// <summary>
    /// Initializes a new instance of <see cref="WorkflowDbContext"/>.
    /// </summary>
    public WorkflowDbContext(
        DbContextOptions<WorkflowDbContext> options,
        ICurrentSchema? currentSchema = null,
        IOptions<WorkflowExecutionOptions>? executionOptions = null,
        ILogger<WorkflowDbContext>? logger = null)
        : base(options)
    {
        _currentSchema = currentSchema;
        _instanceDataWriteOptions = executionOptions?.Value.InstanceDataWrite ?? new InstanceDataWriteOptions();
        _instanceDataWriteLogger = logger ?? NullLogger<WorkflowDbContext>.Instance;
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
    /// Gets or sets the background jobs.
    /// </summary>
    /// <remarks>
    /// <b>[DEPRECATED]</b> Birincil store <c>MessagingDbContext.BackgroundJobs</c>'a taşındı (sys_queues).
    /// Bu DbSet yalnızca geriye dönük uyum için tutulmaktadır.
    /// </remarks>
    [Obsolete("Use MessagingDbContext.BackgroundJobs. This DbSet will be removed in a future major version.")]
    public virtual DbSet<BackgroundJobInfo> BackgroundJobs { get; set; }

    /// <summary>
    /// Gets the current schema name as seen by this context instance.
    /// Exposed for <see cref="SchemaAwareModelCacheKeyFactory"/> so it can build
    /// the cache key without any DI dependency.
    /// </summary>
    public string? CurrentSchemaName => _currentSchema?.Name;

    /// <summary>
    /// InstanceData write funnel: when the change tracker holds new <see cref="InstanceData"/>
    /// rows, the save runs inside a transaction with a per-instance <c>FOR UPDATE</c> row lock
    /// (POC-validated) — the funnel assigns monotonic VersionNos, rebases stale semantic
    /// versions onto the real head, and demotes stale latest rows before the inserts execute.
    /// Saves without new InstanceData rows are untouched. See <see cref="InstanceDataWriteFunnel"/>.
    /// </summary>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        if (!InstanceDataWriteFunnel.HasPendingInstanceData(this))
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        // The row lock is transaction-scoped: with an ambient UoW transaction the lock rides
        // it (held until that commit — same profile as the former advisory-lock trigger);
        // without one, open a local transaction so lock + inserts commit atomically.
        if (Database.CurrentTransaction is not null)
        {
            await InstanceDataWriteFunnel.ApplyAsync(
                this, _instanceDataWriteOptions, _instanceDataWriteLogger, cancellationToken);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        await InstanceDataWriteFunnel.ApplyAsync(
            this, _instanceDataWriteOptions, _instanceDataWriteLogger, cancellationToken);
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

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