using BBT.Workflow.Runtime;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Runs a routed gateway's local branch as its target domain.
/// </summary>
/// <remarks>
/// With multi-domain hosting a cross-domain call between two co-hosted domains takes the local
/// branch, so the in-process call must serve the target domain, not the caller's. The call is started
/// inside the scope: an async method captures the execution context at its first await, so the domain
/// stays in effect for its whole run while the caller's own scope is restored on return.
/// </remarks>
internal static class LocalDomainCall
{
    /// <summary>Starts <paramref name="call"/> with <paramref name="domain"/> as the current domain.</summary>
    public static T Run<T>(string? domain, Func<T> call)
    {
        using (DomainScope.Begin(domain))
        {
            return call();
        }
    }
}
