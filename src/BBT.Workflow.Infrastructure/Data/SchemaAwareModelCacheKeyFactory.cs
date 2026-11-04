using BBT.Aether.Uow.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BBT.Workflow.Data;

/// <summary>
/// EF Core model cache key factory for <see cref="WorkflowDbContext"/>'s two model forms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runtime</b> contexts (<see cref="WorkflowDbContext.UsesSchemaPlaceholderModel"/>) map tables to
/// Aether's schema placeholder, so every schema shares one key, one model and one compiled-query set.
/// The schema is written into each command by Aether's <c>QualifiedNamesCommandInterceptor</c>.
/// </para>
/// <para>
/// <b>Migration, design-time and directly resolved</b> contexts write the schema into their table
/// mappings, so their model differs per schema and the key carries the schema name. These contexts are
/// short-lived and few, so their per-schema models do not crowd the cache the runtime reads from.
/// </para>
/// <para>
/// The factory is stateless and reads everything from the context instance, so it works for DI-managed
/// contexts and for contexts built by hand (<c>MultiSchemaMigrator</c>) alike.
/// </para>
/// </remarks>
public sealed class SchemaAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        if (context is WorkflowDbContext { UsesSchemaPlaceholderModel: true })
        {
            return (context.GetType(), AetherSchemaModel.Placeholder, designTime);
        }

        var schemaName = context is WorkflowDbContext workflowCtx
            ? workflowCtx.CurrentSchemaName ?? "public"
            : "public";

        return (context.GetType(), schemaName, designTime);
    }
}
