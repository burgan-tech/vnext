using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Flow-level configuration container. Consolidates author-controlled, workflow-scoped
/// settings under a single <c>config</c> object on the flow definition, so future
/// flow-level options can be added without widening the root shape.
/// JSON shape: <c>"config": { "functionCache": { "ttlSeconds": 120 } }</c>.
/// </summary>
public sealed class WorkflowConfig
{
    private WorkflowConfig()
    {
    }

    [JsonConstructor]
    private WorkflowConfig(FunctionCacheDefinition? functionCache)
    {
        FunctionCache = functionCache;
    }

    /// <summary>
    /// Optional author-controlled cache tuning for the built-in instance functions
    /// (data, view, schema, ...). Null means host defaults apply.
    /// </summary>
    [JsonInclude] [JsonPropertyName("functionCache")]
    public FunctionCacheDefinition? FunctionCache { get; private set; }
}
