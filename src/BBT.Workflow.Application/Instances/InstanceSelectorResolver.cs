using BBT.Workflow.Filtering;

namespace BBT.Workflow.Instances;

/// <summary>
/// Default <see cref="IInstanceSelectorResolver"/>. Scopes the caller's filter to the target workflow
/// (adds a <c>flow = {workflow}</c> condition) and runs the instance filter engine
/// (<see cref="IInstanceRepository.FindByFilterAsync"/>), returning the matched instance's key.
/// </summary>
public sealed class InstanceSelectorResolver(IInstanceRepository instanceRepository) : IInstanceSelectorResolver
{
    /// <inheritdoc />
    public async Task<string?> ResolveKeyAsync(
        string domain,
        string workflow,
        InstanceFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Scope to the target workflow's instances so a shared schema cannot leak other flows' rows.
        var flowCondition = FilterNode.Leaf(new FilterCondition(
            new FilterField(FilterFieldKind.Column, "flow"),
            FilterOperator.Eq,
            [workflow]));

        var scoped = new InstanceFilter(
            FilterNode.All([flowCondition, filter.Root]),
            filter.Order,
            filter.Selection);

        var instance = await instanceRepository.FindByFilterAsync(scoped, cancellationToken);
        return instance?.Key;
    }
}
