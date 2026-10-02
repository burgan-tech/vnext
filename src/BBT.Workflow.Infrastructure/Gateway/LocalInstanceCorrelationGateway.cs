using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Correlation;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Expands a correlation hop that stays inside this runtime, in a fresh DI scope.
/// </summary>
/// <remarks>
/// The scope is what makes the recursion safe: each hop switches <c>ICurrentSchema</c> to its own
/// flow, and resolving the resolver FROM the scope rather than injecting it also breaks the
/// construction cycle resolver → gateway → resolver.
/// <para>
/// Note what this deliberately does NOT do: open a unit of work. The caller already wrapped the
/// hop in <c>ExecuteInIsolatedUnitOfWorkAsync</c> before taking its concurrency slot, so the
/// branch owns exactly one connection and every nested hop inside it inherits that one. Beginning
/// another here would make open connections scale with width × depth instead of width.
/// </para>
/// </remarks>
public sealed class LocalInstanceCorrelationGateway(IServiceScopeFactory serviceScopeFactory)
    : IInstanceCorrelationGateway
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<CorrelationBatchResult>>> ResolveAsync(
        string domain,
        string flow,
        CorrelationBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        return serviceScopeFactory.ExecuteInScopeRawAsync(
            async (sp, ct) =>
            {
                var resolver = sp.GetRequiredService<IInstanceCorrelationResolver>();
                return await resolver.ResolveAsync(domain, flow, request, ct);
            },
            cancellationToken);
    }
}
