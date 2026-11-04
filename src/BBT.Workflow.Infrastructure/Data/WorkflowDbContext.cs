using BBT.Aether.Domain.Entities;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Aether.Domain.Events;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Persistence;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data.ValueConverters;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

/// <summary>
/// Database context for workflow engine persistence.
/// Supports multi-schema architecture for multi-tenancy.
///
/// Schema isolation never uses <c>SET search_path</c>, so the context is safe under PgBouncer
/// transaction-mode pooling. It takes one of two forms, depending on how the options were built:
/// <list type="bullet">
///   <item><b>Runtime</b> (Aether unit of work, options carry <see cref="AetherSchemaModelOptionsExtension"/>):
///   tables map to Aether's schema placeholder and <c>QualifiedNamesCommandInterceptor</c> writes the
///   context's schema into every command. All schemas share ONE model and one compiled-query set.</item>
///   <item><b>Migration / design time / directly resolved</b> (no extension): the schema from
///   <see cref="ICurrentSchema"/> is written into the table mappings, so migrations and
///   <c>EnsureCreated</c> target that schema exactly as before. These models are cached per schema.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// One model per schema at runtime overflowed EF Core's internal cache (<c>SizeLimit</c> 10240; a model
/// costs 250, a compiled query 10) once a domain had a few dozen flows, so models and queries were
/// rebuilt on almost every request against a rarely used schema. See
/// <c>docs/runtime/multi-schema-model.md</c>.
/// </para>
/// <para>
/// <b>DEPRECATED:</b> <see cref="BackgroundJobInfo"/> / <see cref="IHasEfCoreBackgroundJobs"/>
/// implementasyonu bu context'ten kaldırılacak. Birincil store <c>MessagingDbContext</c>'tir
/// (sys_queues.BackgroundJobs). Bu DbSet ve interface yalnızca geriye dönük uyum için tutulmaktadır.
/// </para>
/// </remarks>
public class WorkflowDbContext : AetherDbContext<WorkflowDbContext>, IHasEfCoreBackgroundJobs
{
    private readonly ICurrentSchema? _currentSchema;

    /// <summary>
    /// Initializes a new instance of <see cref="WorkflowDbContext"/>.
    /// </summary>
    public WorkflowDbContext(
        DbContextOptions<WorkflowDbContext> options,
        ICurrentSchema? currentSchema = null)
        : base(options)
    {
        _currentSchema = currentSchema;
        UsesSchemaPlaceholderModel = options.FindExtension<AetherSchemaModelOptionsExtension>() is not null;
    }

    /// <summary>
    /// <c>true</c> when the options come from an Aether unit of work: the model maps tables to the schema
    /// placeholder and is shared by every schema. <c>false</c> for migration, design-time and directly
    /// resolved contexts, whose model is bound to <see cref="CurrentSchemaName"/>.
    /// </summary>
    public bool UsesSchemaPlaceholderModel { get; }

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
    /// Gets or sets the error boundary incidents recorded on instances (one row per incident).
    /// </summary>
    public virtual DbSet<InstanceIncident> InstanceIncidents { get; set; }

    /// <summary>
    /// Gets or sets the per-instance x-encryption secrets (key + hash salt). Read and written only by
    /// <see cref="BBT.Workflow.Encryption.InstanceSecretStore"/>; never served.
    /// </summary>
    public virtual DbSet<BBT.Workflow.Encryption.InstanceSecret> InstanceSecrets { get; set; }

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
    /// Gets the current schema name as seen by this context instance — the schema its commands target,
    /// whatever the model form. Read by <see cref="SchemaAwareModelCacheKeyFactory"/>, the migration SQL
    /// generator and the raw-SQL writers, without any DI dependency.
    /// </summary>
    public string? CurrentSchemaName => _currentSchema?.Name;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(null);

        // With the Aether extension the base sets the placeholder as the default schema.
        base.OnModelCreating(builder);

        // A null schema lets every table inherit that placeholder; the interceptor qualifies it per command.
        var schema = UsesSchemaPlaceholderModel ? null : _currentSchema?.Name;
        builder.ConfigureWorkflow(schema);
        builder.ConfigureBackgroundJob(schema);
    }
}