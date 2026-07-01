using BBT.Workflow.Monitor.Instances.DTOs;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>
/// Pure recursion engine for assembling an instance hierarchy tree.
/// I/O (cross-schema child loading) is injected as <paramref name="fetchChildren"/> so the engine is testable.
/// </summary>
public static class InstanceHierarchyBuilder
{
    /// <summary>
    /// Recursively populates <paramref name="node"/>'s <see cref="MonitorHierarchyNode.Children"/>
    /// by calling <paramref name="fetchChildren"/> at each level until <paramref name="maxDepth"/> is reached
    /// or every node has already been visited (cycle guard).
    /// </summary>
    /// <param name="node">The node whose children should be populated.</param>
    /// <param name="fetchChildren">(parentNode, currentDepth) => direct child nodes.</param>
    /// <param name="maxDepth">Maximum recursion depth (cycle/runaway guard).</param>
    /// <param name="visited">Instance IDs already placed in the tree (cycle guard).</param>
    /// <param name="depth">Current recursion depth (starts at 0 for the root's children).</param>
    public static async Task PopulateAsync(
        MonitorHierarchyNode node,
        Func<MonitorHierarchyNode, int, Task<List<MonitorHierarchyNode>>> fetchChildren,
        int maxDepth,
        HashSet<Guid> visited,
        int depth = 0)
    {
        if (depth >= maxDepth) return;

        var children = await fetchChildren(node, depth);
        foreach (var child in children)
        {
            if (!visited.Add(child.InstanceId)) continue; // already in tree → skip (cycle)
            node.Children.Add(child);
            await PopulateAsync(child, fetchChildren, maxDepth, visited, depth + 1);
        }
    }
}
