using System.Collections;
using System.Dynamic;
using System.Text.Json;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// Merge strategy for collection types including dictionaries and enumerable objects.
/// Handles dictionary merging with recursive value merging.
/// List-like collections are replaced entirely rather than merged to ensure deterministic updates.
/// </summary>
public class CollectionMergeStrategy : IMergeStrategy
{
    /// <summary>
    /// Merges two collections. Dictionaries are deep merged, while list-like collections are replaced.
    /// </summary>
    /// <param name="target">The target collection.</param>
    /// <param name="source">The source collection.</param>
    /// <returns>The merged collection (for dictionaries) or the source collection (for lists).</returns>
    public object? Merge(object? target, object? source)
    {
        if (target is not IEnumerable targetCollection || source is not IEnumerable sourceCollection)
            return source;

        return MergeCollections(targetCollection, sourceCollection);
    }

    /// <summary>
    /// Merges two collections. Dictionaries are deep merged, while list-like collections are replaced.
    /// </summary>
    /// <param name="target">The target collection.</param>
    /// <param name="source">The source collection.</param>
    /// <returns>The merged collection (for dictionaries) or the source collection (for lists).</returns>
    private static object? MergeCollections(IEnumerable target, IEnumerable source)
    {
        // Handle Dictionary<string, object> types - deep merge
        if (target is IDictionary<string, object?> targetDict && source is IDictionary<string, object?> sourceDict)
        {
            var result = new ExpandoObject() as IDictionary<string, object?>;

            // Add all target properties
            foreach (var kvp in targetDict)
            {
                result[kvp.Key] = kvp.Value;
            }

            // Merge source properties
            foreach (var kvp in sourceDict)
            {
                result[kvp.Key] = result.TryGetValue(kvp.Key, out var value)
                    ? ObjectMerger.MergeValues(value, kvp.Value)
                    : kvp.Value;
            }

            return (ExpandoObject)result;
        }

        // For list-like collections, replace entirely with source
        // This ensures deterministic updates and allows removing items from lists
        try
        {
            return source.Cast<object>().ToList();
        }
        catch
        {
            // If casting fails, return source as-is
            return source;
        }
    }
}