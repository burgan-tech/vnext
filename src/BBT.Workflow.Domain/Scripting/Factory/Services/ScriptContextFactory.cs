using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Default implementation of IScriptContextFactory that provides fluent builder capabilities
/// and handles ScriptContext creation with various data sources.
/// </summary>
public sealed class ScriptContextFactory(
    IComponentCacheStore componentCacheStore,
    ILogger<ScriptContext> logger,
    IRequestRawBodyProvider? rawBodyProvider = null) : IScriptContextFactory
{
    /// <summary>
    /// Creates a new fluent builder for constructing ScriptContext instances.
    /// </summary>
    /// <returns>A new ScriptContextBuilder instance for fluent configuration.</returns>
    public IScriptContextBuilder NewBuilder(IInstanceRepository  instanceRepository)
    {
        return new ScriptContextBuilder(componentCacheStore, instanceRepository, logger, rawBodyProvider);
    }
}
