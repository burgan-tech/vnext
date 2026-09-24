using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Metrics;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

/// <summary>
/// Domain-wide telemetry context, owning the function-execution journal
/// (vnext-client-sdk-core#60, item C1). Fixed <c>sys_metrics</c> schema — the same
/// domain-wide (not per-flow) posture as <see cref="MessagingDbContext"/>'s <c>sys_queues</c>,
/// because a function can run domain-scoped with no flow at all. Migrated by its own
/// <c>MigrateMetricsDbContext()</c> call at deploy time.
/// </summary>
public class MetricsDbContext(
    DbContextOptions<MetricsDbContext> options)
    : AetherDbContext<MetricsDbContext>(options)
{
    /// <summary>The function-execution journal (one row per domain-function invocation).</summary>
    public virtual DbSet<FunctionExecution> FunctionExecutions { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema(FunctionExecution.SchemaName);
        base.OnModelCreating(builder);

        builder.Entity<FunctionExecution>(b =>
        {
            b.ToTable("FunctionExecutions");
            b.HasKey(e => e.Id);

            b.Property(e => e.Domain).HasMaxLength(256).IsRequired();
            b.Property(e => e.FunctionKey).HasMaxLength(256).IsRequired();
            b.Property(e => e.FunctionVersion).HasMaxLength(64);
            b.Property(e => e.Scope).HasMaxLength(1).IsRequired();
            b.Property(e => e.Workflow).HasMaxLength(256);
            b.Property(e => e.ErrorCode).HasMaxLength(512);
            b.Property(e => e.TraceId).HasMaxLength(64);
            b.Property(e => e.CreatedBy).HasMaxLength(256);
            b.Property(e => e.CreatedByBehalfOf).HasMaxLength(256);

            // Primary (and only) access pattern of the D endpoints: one function's runs, newest first,
            // bounded by a from/to window. The composite btree serves the filter, the order and the
            // keyset paging in one index.
            b.HasIndex(e => new { e.FunctionKey, e.InvokedAt });
        });
    }
}
