using BBT.Aether.MultiSchema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BBT.Workflow.Data;

/// <summary>
/// EF Core model cache key factory that partitions the compiled model cache by the current schema name.
///
/// Replaces <c>NpgsqlSchemaConnectionInterceptor</c> / <c>PgBouncerSafeSchemaCommandInterceptor</c>
/// approach: instead of injecting <c>SET search_path</c> into SQL commands, this factory ensures
/// a separate compiled model is built and cached for each schema. Entity table mappings are fully
/// qualified with the schema name in <c>OnModelCreating</c>, so no session-level <c>search_path</c>
/// directive is ever sent — making the context safe under PgBouncer transaction-mode pooling.
///
/// <para>
/// Schema name is read directly from the <see cref="WorkflowDbContext"/> instance passed to
/// <see cref="Create"/>. This avoids any DI lifetime conflict: the factory itself is stateless
/// and requires no injected dependencies. It works for both DI-managed contexts (runtime) and
/// manually constructed contexts (migration path via <c>MultiSchemaMigrator</c>).
/// </para>
/// </summary>
public sealed class SchemaAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        var schemaName = context is WorkflowDbContext workflowCtx
            ? workflowCtx.CurrentSchemaName ?? "public"
            : "public";

        return (context.GetType(), schemaName, designTime);
    }
}
