using System.Collections;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BBT.Workflow.Shared.Merging;

/// <summary>
/// Factory class responsible for selecting the appropriate merge strategy based on object types.
/// Uses singleton instances for optimal performance in high-frequency scenarios.
/// </summary>
public static class MergeStrategyFactory
{
    // Singleton instances for performance optimization - initialized once at startup
    private static readonly IMergeStrategy _expandoObjectStrategy = new ExpandoObjectMergeStrategy();
    private static readonly IMergeStrategy _jsonElementStrategy = new JsonElementMergeStrategy();
    private static readonly IMergeStrategy _collectionStrategy = new CollectionMergeStrategy();
    private static readonly IMergeStrategy _defaultStrategy = new DefaultMergeStrategy();

    // Cache for IsListLikeCollection results to avoid repeated reflection in hot paths
    private static readonly ConcurrentDictionary<Type, bool> _listLikeCache = new();

    /// <summary>
    /// Gets the appropriate merge strategy for the given target and source objects.
    /// Returns singleton instances to avoid memory allocations in high-frequency scenarios.
    /// </summary>
    /// <param name="target">The target object.</param>
    /// <param name="source">The source object.</param>
    /// <returns>The most suitable merge strategy for the given object types.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IMergeStrategy GetStrategy(object? target, object? source)
    {
        // Handle null cases
        if (target == null || source == null)
            return _defaultStrategy;

        // CRITICAL: Check for structural type mismatch first.
        // If one is an array/list and the other is an object, source wins (override behavior).
        // This prevents incorrect merging when types fundamentally differ.
        if (HasStructuralTypeMismatch(target, source))
            return _defaultStrategy;

        // Handle ExpandoObject merging (highest priority)
        if (target is ExpandoObject && source is ExpandoObject)
            return _expandoObjectStrategy;

        // Handle JsonElement merging
        if (target is JsonElement targetElement && source is JsonElement sourceElement)
        {
            // Check JsonElement structural mismatch (array vs object)
            if (HasJsonElementTypeMismatch(targetElement, sourceElement))
                return _defaultStrategy;
            return _jsonElementStrategy;
        }

        // Handle mixed JsonElement and ExpandoObject scenarios
        if ((target is JsonElement && source is ExpandoObject) ||
            (target is ExpandoObject && source is JsonElement))
        {
            return _jsonElementStrategy;
        }

        // Handle collections (but not strings, as they implement IEnumerable)
        // Only merge if both are truly list-like collections (not dictionaries treated as collections)
        if (IsListLikeCollection(target) && IsListLikeCollection(source))
            return _collectionStrategy;

        // Handle dictionary-like collections
        if (IsDictionaryLike(target) && IsDictionaryLike(source))
            return _collectionStrategy;

        return _defaultStrategy;
    }

    /// <summary>
    /// Checks if target and source have fundamentally different structural types.
    /// Array/List vs Object mismatch should result in source override (not merge).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasStructuralTypeMismatch(object target, object source)
    {
        var targetIsArray = IsListLikeCollection(target);
        var sourceIsArray = IsListLikeCollection(source);

        // If one is array-like and other is not, they are structurally incompatible
        return targetIsArray != sourceIsArray;
    }

    /// <summary>
    /// Checks if two JsonElements have different value kinds (array vs object).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasJsonElementTypeMismatch(JsonElement target, JsonElement source)
    {
        // Array vs Object mismatch
        if ((target.ValueKind == JsonValueKind.Array && source.ValueKind == JsonValueKind.Object) ||
            (target.ValueKind == JsonValueKind.Object && source.ValueKind == JsonValueKind.Array))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if the object is a list-like collection (array, list, etc.) but NOT a dictionary or string.
    /// Uses caching to avoid repeated reflection overhead in high-frequency scenarios.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsListLikeCollection(object obj)
    {
        // Fast path: exclude strings (they implement IEnumerable)
        if (obj is string)
            return false;

        // Fast path: exclude dictionary-like objects (ExpandoObject, IDictionary)
        if (obj is ExpandoObject || obj is IDictionary)
            return false;

        // Fast path: exclude IDictionary<string, object?> which ExpandoObject implements
        if (obj is IDictionary<string, object?>)
            return false;

        // Fast path: check for array or IList
        if (obj is Array || obj is IList)
            return true;

        // Check if it's an enumerable but not a dictionary
        if (obj is IEnumerable)
        {
            // Use cached result to avoid repeated reflection
            var type = obj.GetType();
            return _listLikeCache.GetOrAdd(type, EvaluateListLikeType);
        }

        return false;
    }

    /// <summary>
    /// Evaluates whether a type is list-like (used for caching).
    /// This method performs reflection and is only called once per type.
    /// </summary>
    private static bool EvaluateListLikeType(Type type)
    {
        // Check if it's a generic enumerable of KeyValuePair (dictionary-like)
        if (type.IsGenericType)
        {
            var genericArgs = type.GetGenericArguments();
            if (genericArgs.Length == 1 && genericArgs[0].IsGenericType &&
                genericArgs[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if the object is dictionary-like (ExpandoObject, IDictionary, etc.)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDictionaryLike(object obj)
    {
        return obj is ExpandoObject ||
               obj is IDictionary ||
               obj is IDictionary<string, object?>;
    }
}
