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
/// <para>
/// Depth guard: JSON-origin trees are acyclic by construction, but a script CAN hand-craft a
/// self-referencing expando and route it into a clone path. Unbounded recursion there would be a
/// StackOverflowException — an uncatchable process kill (the legacy JSON round-trip degraded to
/// silent cycle-dropping via <c>ReferenceHandler.IgnoreCycles</c> instead). The guard converts
/// that into a diagnosable exception at depth 256, matching the serializer's <c>MaxDepth</c>
/// convention used elsewhere in the runtime.
/// </para>
/// </remarks>
public static class DynamicCloner
{
    private const int MaxDepth = 256;

    /// <summary>
    /// Deep-clones the given <c>ToDynamic</c>-shaped value. Containers are copied recursively;
    /// leaves are returned as-is (immutable, safe to share).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The graph nests deeper than 256 levels — almost certainly a script-crafted cycle.
    /// </exception>
    public static object? DeepClone(object? value) => DeepClone(value, depth: 0);

    private static object? DeepClone(object? value, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"DynamicCloner.DeepClone exceeded the maximum depth of {MaxDepth} — the value graph " +
                "is nested too deeply or contains a cycle (e.g. a script assigned an expando into itself).");
        }

        return value switch
        {
            ExpandoObject expando => CloneExpando(expando, depth + 1),
            List<object?> list => list.ConvertAll(item => DeepClone(item, depth + 1)),
            object?[] array => Array.ConvertAll(array, item => DeepClone(item, depth + 1)),
            _ => value // leaf: string / number / bool / null / JsonElement — immutable, share
        };
    }

    private static ExpandoObject CloneExpando(ExpandoObject source, int depth)
    {
        var clone = new ExpandoObject();
        var target = (IDictionary<string, object?>)clone;
        foreach (var (key, value) in (IDictionary<string, object?>)source)
            target[key] = DeepClone(value, depth);
        return clone;
    }
}
