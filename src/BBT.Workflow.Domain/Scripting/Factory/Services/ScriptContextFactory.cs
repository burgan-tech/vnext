using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Default implementation of IScriptContextFactory that provides fluent builder capabilities
/// and handles ScriptContext creation with various data sources.
/// </summary>
/// <remarks>
/// Registered scoped (see <c>TaskServiceCollectionExtensions.AddScriptingServices</c>) specifically so it
/// can safely depend on the scoped <see cref="IRelatedInstanceReader"/> and
/// <see cref="IInstanceCorrelationRepository"/> gateway services without becoming a captive dependency.
/// It carries no mutable state of its own, so scoped-vs-singleton has no other observable effect.
/// </remarks>
public sealed class ScriptContextFactory(
    IComponentCacheStore componentCacheStore,
    ILogger<ScriptContext> logger,
    ILogger<RelatedInstanceAccessor> relatedLogger,
    IRequestRawBodyProvider? rawBodyProvider = null,
    IRelatedInstanceReader? relatedInstanceReader = null,
    IInstanceCorrelationRepository? correlationRepository = null,
    IOptions<RelatedAccessOptions>? relatedAccessOptions = null,
    ISensitiveDataScrubberAccessor? scrubberAccessor = null) : IScriptContextFactory
{
    /// <summary>
    /// Creates a new fluent builder for constructing ScriptContext instances.
    /// </summary>
    /// <returns>A new ScriptContextBuilder instance for fluent configuration.</returns>
    public IScriptContextBuilder NewBuilder(IInstanceRepository  instanceRepository)
    {
        return new ScriptContextBuilder(
            componentCacheStore,
            instanceRepository,
            logger,
            relatedLogger,
            rawBodyProvider,
            relatedInstanceReader,
            correlationRepository,
            relatedAccessOptions,
            scrubberAccessor);
    }
}
