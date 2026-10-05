using BBT.Aether.Results;

namespace BBT.Workflow.Instances.Correlation;

/// <summary>
/// Expands one hop of a correlation tree: every requested instance of ONE flow in ONE domain, with
/// their descendants resolved recursively.
/// </summary>
/// <remarks>
/// The unit of work is the hop, not the node. A level is grouped by <c>(domain, flow)</c> before it
/// reaches here, so one call answers for many instances; and because the implementation recurses on
/// its own, a branch that crosses a domain boundary costs ONE remote call for that whole branch
/// rather than one per instance per level. Same contract as <c>IHumanTaskLeafResolver</c>, which
/// solved this shape first.
/// </remarks>
public interface IInstanceCorrelationResolver
{
    /// <summary>Expands a batch of instances belonging to <paramref name="domain"/>'s <paramref name="flow"/>.</summary>
    Task<Result<IReadOnlyList<CorrelationBatchResult>>> ResolveAsync(
        string domain,
        string flow,
        CorrelationBatchRequest request,
        CancellationToken cancellationToken = default);
}
