using System.Text.Json;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Represents cached SubFlow error context stored in Instance.ExtraProperties.
/// This data is set by HandleSubFlowStep when starting a SubFlow and retrieved
/// by SubflowCompletionService when the SubFlow completes with an error.
/// </summary>
public sealed record SubFlowCachedErrorContext
{
    /// <summary>
    /// Indicates whether the SubFlow has a custom error boundary defined.
    /// </summary>
    public bool HasErrorBoundary { get; init; }

    /// <summary>
    /// Indicates whether errors should be propagated to the parent workflow.
    /// </summary>
    public bool PropagateToParent { get; init; }

    /// <summary>
    /// Indicates whether child error details should be included in the parent error context.
    /// </summary>
    public bool IncludeChildErrorDetails { get; init; }

    /// <summary>
    /// The transition key to trigger in the parent workflow when an error occurs.
    /// </summary>
    public string? ParentTransition { get; init; }

    /// <summary>
    /// The key of the parent state where the SubFlow was initiated.
    /// </summary>
    public string? ParentStateKey { get; init; }

    /// <summary>
    /// The key of the parent workflow.
    /// </summary>
    public string? ParentWorkflowKey { get; init; }

    /// <summary>
    /// Creates a SubFlowCachedErrorContext from a dictionary.
    /// </summary>
    public static SubFlowCachedErrorContext FromDictionary(Dictionary<string, object?> dict)
    {
        return new SubFlowCachedErrorContext
        {
            HasErrorBoundary = GetBoolValue(dict, "HasErrorBoundary"),
            PropagateToParent = GetBoolValue(dict, "PropagateToParent", defaultValue: true),
            IncludeChildErrorDetails = GetBoolValue(dict, "IncludeChildErrorDetails"),
            ParentTransition = GetStringValue(dict, "ParentTransition"),
            ParentStateKey = GetStringValue(dict, "ParentStateKey"),
            ParentWorkflowKey = GetStringValue(dict, "ParentWorkflowKey")
        };
    }

    /// <summary>
    /// Creates a SubFlowCachedErrorContext from a JsonElement.
    /// </summary>
    public static SubFlowCachedErrorContext FromJsonElement(JsonElement element)
    {
        return new SubFlowCachedErrorContext
        {
            HasErrorBoundary = GetBoolFromJson(element, "HasErrorBoundary"),
            PropagateToParent = GetBoolFromJson(element, "PropagateToParent", defaultValue: true),
            IncludeChildErrorDetails = GetBoolFromJson(element, "IncludeChildErrorDetails"),
            ParentTransition = GetStringFromJson(element, "ParentTransition"),
            ParentStateKey = GetStringFromJson(element, "ParentStateKey"),
            ParentWorkflowKey = GetStringFromJson(element, "ParentWorkflowKey")
        };
    }

    private static bool GetBoolValue(Dictionary<string, object?> dict, string key, bool defaultValue = false)
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value switch
            {
                bool b => b,
                JsonElement je when je.ValueKind == JsonValueKind.True => true,
                JsonElement je when je.ValueKind == JsonValueKind.False => false,
                _ => defaultValue
            };
        }
        return defaultValue;
    }

    private static string? GetStringValue(Dictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var value))
        {
            return value switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                _ => null
            };
        }
        return null;
    }

    private static bool GetBoolFromJson(JsonElement element, string key, bool defaultValue = false)
    {
        if (element.TryGetProperty(key, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue
            };
        }
        return defaultValue;
    }

    private static string? GetStringFromJson(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }
}

