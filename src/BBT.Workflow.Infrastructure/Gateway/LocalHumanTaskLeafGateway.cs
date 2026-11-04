using BBT.Aether.Results;
using BBT.Workflow.Instances.HumanTask;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Resolves a human-task descent hop that stays inside this runtime, in a fresh DI scope.
/// </summary>
/// <remarks>
/// The scope and the unit of work are what make the recursion safe: each hop gets its own DbContext
/// (<c>ExecuteInIsolatedUnitOfWorkAsync</c>), each level switches <c>ICurrentSchema</c> to its own
/// flow, and resolving the resolver from the scope (rather than injecting it) also breaks the
/// construction cycle resolver → gateway → resolver.
/// </remarks>
public sealed class LocalHumanTaskLeafGateway(IServiceScopeFactory serviceScopeFactory) : IHumanTaskLeafGateway
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
        string domain,
        string flow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken = default)
    {
        return serviceScopeFactory.ExecuteInIsolatedUnitOfWorkAsync(
            async (sp, ct) =>
            {
                var resolver = sp.GetRequiredService<IHumanTaskLeafResolver>();
                return await resolver.ResolveAsync(domain, flow, request, ct);
            },
            cancellationToken);
    }
}
