using System.Dynamic;
using System.Text.Json;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// Merge strategy for JsonElement instances and mixed JsonElement/ExpandoObject scenarios.
/// Handles object merging (deep merge), array replacement, and type conversions.
/// Arrays are replaced entirely rather than merged to ensure deterministic updates.
/// </summary>
public class JsonElementMergeStrategy : IMergeStrategy
{
    /// <summary>
    /// Merges JsonElement objects and handles mixed JsonElement/ExpandoObject scenarios.
    /// </summary>
    /// <param name="target">The target value (JsonElement or ExpandoObject).</param>
    /// <param name="source">The source value (JsonElement or ExpandoObject).</param>
    /// <returns>The merged result.</returns>
    public object? Merge(object? target, object? source)
    {
        if (target == null || source == null)
            return source ?? target;

        return MergeJsonElements(target, source);
    }

    /// <summary>
    /// Merges JsonElement objects and handles mixed JsonElement/ExpandoObject scenarios.
    /// </summary>
    /// <param name="target">The target value (JsonElement or ExpandoObject).</param>
    /// <param name="source">The source value (JsonElement or ExpandoObject).</param>
    /// <returns>The merged result.</returns>
    private static object? MergeJsonElements(object target, object source)
    {
        // Handle JsonElement to JsonElement merging
        if (target is JsonElement targetElement && source is JsonElement sourceElement)
        {
            // Handle object merging
            if (targetElement.ValueKind == JsonValueKind.Object && sourceElement.ValueKind == JsonValueKind.Object)
            {
                var targetExpandoFromJson = JsonSerializer.Deserialize<ExpandoObject>(targetElement.GetRawText(), ScriptContext.JsonScriptBodyOptions);
                var sourceExpandoFromJson = JsonSerializer.Deserialize<ExpandoObject>(sourceElement.GetRawText(), ScriptContext.JsonScriptBodyOptions);

                if (targetExpandoFromJson != null && sourceExpandoFromJson != null)
                {
                    var expandoStrategy = new ExpandoObjectMergeStrategy();
                    return expandoStrategy.Merge(targetExpandoFromJson, sourceExpandoFromJson);
                }
            }

            // Handle array replacement - source array completely replaces target array
            // This ensures deterministic updates and allows removing items from arrays
            if (targetElement.ValueKind == JsonValueKind.Array && sourceElement.ValueKind == JsonValueKind.Array)
            {
                return sourceElement;
            }

            // For other JsonElement types, source takes precedence
            return sourceElement;
        }

        // Handle mixed JsonElement and ExpandoObject scenarios
        if (target is JsonElement targetJson && source is ExpandoObject sourceExp)
        {
            if (targetJson.ValueKind == JsonValueKind.Object)
            {
                var targetExpandoFromMixed = JsonSerializer.Deserialize<ExpandoObject>(targetJson.GetRawText(), ScriptContext.JsonScriptBodyOptions);
                if (targetExpandoFromMixed != null)
                {
                    var expandoStrategy = new ExpandoObjectMergeStrategy();
                    return expandoStrategy.Merge(targetExpandoFromMixed, sourceExp);
                }
            }
            return sourceExp;
        }

        if (target is ExpandoObject targetExp && source is JsonElement sourceJson)
        {
            if (sourceJson.ValueKind == JsonValueKind.Object)
            {
                var sourceExpandoFromMixed = JsonSerializer.Deserialize<ExpandoObject>(sourceJson.GetRawText(), ScriptContext.JsonScriptBodyOptions);
                if (sourceExpandoFromMixed != null)
                {
                    var expandoStrategy = new ExpandoObjectMergeStrategy();
                    return expandoStrategy.Merge(targetExp, sourceExpandoFromMixed);
                }
            }
            return sourceJson;
        }

        // For all other cases, source takes precedence
        return source;
    }
}
