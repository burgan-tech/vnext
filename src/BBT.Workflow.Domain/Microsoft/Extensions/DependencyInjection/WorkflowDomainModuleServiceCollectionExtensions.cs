using BBT.Workflow.Canonicalization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Rules;
using BBT.Workflow.Runtime;
using BBT.Workflow.Validation;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up domain services in an <see cref="IServiceCollection" />.
/// </summary>
public static class WorkflowDomainModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds the domain module services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddDomainModule(
        this IServiceCollection services)
    {
        services.AddAetherDomain();

        services.Configure<RuntimeOptions>(opt =>
        {
            opt.Schemas.Add(RuntimeSysSchemaInfo.Flows, "sys_flows", typeof(Workflow));
            opt.Schemas.Add(RuntimeSysSchemaInfo.Functions, "sys_functions", typeof(Function));
            opt.Schemas.Add(RuntimeSysSchemaInfo.Schemas, "sys_schemas", typeof(SchemaDefinition));
            opt.Schemas.Add(RuntimeSysSchemaInfo.Tasks, "sys_tasks", typeof(WorkflowTask));
            opt.Schemas.Add(RuntimeSysSchemaInfo.Views, "sys_views", typeof(View));
            opt.Schemas.Add(RuntimeSysSchemaInfo.Extensions, "sys_extensions", typeof(Extension));
        });

        services.AddSingleton<IRuntimeInfoProvider, RuntimeInfoProvider>();
        services.AddSingleton<WorkflowValidator>();
        services.AddSingleton<IResultRuleEngine<State>, ResultRuleEngine<State>>();
        services.AddSingleton<IJsonSchemaValidator, CachedJsonSchemaValidator>();
        services.AddScoped<IJsonCanonicalizer, JsonCanonicalizer>();
        return services;
    }
}