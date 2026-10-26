using BBT.Aether.Results;

namespace BBT.Workflow.Instances;

/// <summary>
/// Resolves an instance key to its id inside this runtime's own domain. Local-only: it is never exposed by a
/// controller or a routed gateway, reads nothing but the id, and takes no caller headers — so it cannot be used
/// to read instance content and carries no row-security bypass flag (vnext#1107).
/// </summary>
public interface IInstanceIdResolver
{
    /// <summary>
    /// Resolves <paramref name="identifier"/> (an instance id or key) of <paramref name="workflow"/> in
    /// <paramref name="domain"/> to the instance id, within that flow's schema scope.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target flow key.</param>
    /// <param name="identifier">Instance id or key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instance id, or a failure when the instance does not exist.</returns>
    Task<Result<Guid>> ResolveIdAsync(
        string domain,
        string workflow,
        string identifier,
        CancellationToken cancellationToken = default);
}
