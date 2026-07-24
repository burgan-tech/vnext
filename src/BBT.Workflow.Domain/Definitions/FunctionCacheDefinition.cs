using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Workflow-author-controlled cache tuning for the built-in instance functions
/// (data, view, schema, ...). A single TTL covers all of them. The state function is
/// configured host-side (<c>StateFunctionCache</c> options) and is NOT covered by this object.
/// JSON shape on the flow definition: <c>"functionCache": { "ttlSeconds": 120 }</c>.
/// </summary>
public sealed class FunctionCacheDefinition
{
    private FunctionCacheDefinition()
    {
    }

    [JsonConstructor]
    private FunctionCacheDefinition(int? ttlSeconds)
    {
        TtlSeconds = ttlSeconds;
    }

    /// <summary>
    /// Cache TTL in seconds for this workflow's built-in function responses.
    /// Null or non-positive falls back to the host default
    /// (<c>InstanceFunctionCache:DefaultTtlSeconds</c>).
    /// </summary>
    public int? TtlSeconds { get; private set; }
}
