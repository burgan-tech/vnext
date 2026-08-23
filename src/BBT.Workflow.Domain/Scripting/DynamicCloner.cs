using System.Dynamic;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Structural deep clone for <c>ToDynamic</c>-shaped graphs (<see cref="ExpandoObject"/> /
/// <see cref="List{T}"/> of <c>object?</c> / leaf values). Replaces the JSON round-trip clone
/// (serialize + parse + expando rebuild) on the paths where the input is already in that shape:
/// leaves (string, boxed number, bool, null, <c>JsonElement</c>) are immutable and therefore
/// shared, only the containers are copied.
/// </summary>
/// <remarks>
/// Container inventory verified against <c>JsonDocumentExtensions.ConvertToDynamic</c>: objects
/// become <see cref="ExpandoObject"/>, arrays become <c>List&lt;object?&gt;</c>, everything else
/// is a leaf. <c>object?[]</c> is additionally covered because
/// <c>ExpandoObjectJsonConverter.ReadArray</c> materializes arrays as <c>object?[]</c> when an
/// expando tree arrives through deserialization instead of <c>ToDynamic</c> — sharing a mutable
/// array between a parent context and a copy-on-write branch would leak writes.
/// </remarks>
public static class DynamicCloner
{
    /// <summary>
    /// Deep-clones the given <c>ToDynamic</c>-shaped value. Containers are copied recursively;
    /// leaves are returned as-is (immutable, safe to share).
    /// </summary>
    public static object? DeepClone(object? value) => value switch
    {
        ExpandoObject expando => CloneExpando(expando),
        List<object?> list => list.ConvertAll(DeepClone),
        object?[] array => Array.ConvertAll(array, DeepClone),
        _ => value // leaf: string / number / bool / null / JsonElement — immutable, share
    };

    private static ExpandoObject CloneExpando(ExpandoObject source)
    {
        var clone = new ExpandoObject();
        var target = (IDictionary<string, object?>)clone;
        foreach (var (key, value) in (IDictionary<string, object?>)source)
            target[key] = DeepClone(value);
        return clone;
    }
}
