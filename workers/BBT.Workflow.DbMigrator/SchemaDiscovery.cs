using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.DbMigrator;

/// <summary>
/// Shared schema enumeration for every DbMigrator command: the system schemas come from
/// <c>Runtime:Schemas</c>, the domain schemas from the flow definitions in <c>sys_flows</c>
/// (definitions-as-instances: each flow's key is its schema). Extracted from the forward runner so
/// migrate, downgrade and status all act on the same schema set.
/// </summary>
public static class SchemaDiscovery
{
    /// <summary>System schema names from configuration, in declaration order.</summary>
    public static List<string> GetSystemSchemas(IServiceProvider scopedServices) =>
        scopedServices.GetRequiredService<IOptions<RuntimeOptions>>()
            .Value.Schemas.Values.Select(s => s.Schema).ToList();

    /// <summary>
    /// Domain schemas discovered from sys_flows, system schemas excluded. On a discovery failure
    /// (e.g. sys_flows does not exist yet, or the database is unreachable) the schema list is null
    /// and <c>Error</c> carries the cause — the caller decides whether that is a skip (forward
    /// migration of a fresh database) or a hard failure (downgrade), and logs the exception so a
    /// real connectivity/permission problem is distinguishable from the benign fresh-database case.
    /// </summary>
    public static async Task<(List<string>? Schemas, Exception? Error)> TryDiscoverDomainSchemasAsync(
        IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var currentSchema = scopedServices.GetRequiredService<ICurrentSchema>();
        var dbContext = scopedServices.GetRequiredService<WorkflowDbContext>();
        var runtimeOptions = scopedServices.GetRequiredService<IOptions<RuntimeOptions>>();

        using (currentSchema.Change(RuntimeSysSchemaInfo.Flows))
        {
            List<string> domainSchemas;
            try
            {
                domainSchemas = await dbContext.Instances
                    .Where(i => i.Key != null)
                    .Select(i => i.Key!)
                    .Distinct()
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                return (null, ex);
            }

            var systemSchemaNames = runtimeOptions.Value.Schemas.Values
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return (domainSchemas
                .Where(key => !systemSchemaNames.Contains(key))
                .ToList(), null);
        }
    }
}
