using BBT.Workflow.Filtering;

namespace BBT.Workflow.Instances;

/// <summary>
/// Resolves a single workflow instance's business key from an <see cref="InstanceFilter"/> by running
/// the instance filter engine, scoped to a target workflow. Reusable wherever a component needs to
/// correlate to one instance by its data (event mappings that lack a direct key, tasks, functions).
/// </summary>
public interface IInstanceSelectorResolver
{
    /// <summary>
    /// Runs the filter (scoped to the target workflow's instances) and returns the key of the
    /// first/last matching instance, or <c>null</c> when nothing matches.
    /// </summary>
    Task<string?> ResolveKeyAsync(
        string domain,
        string workflow,
        InstanceFilter filter,
        CancellationToken cancellationToken = default);
}
