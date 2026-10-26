using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Resolves an instance key to its id through the repository in the target flow's schema scope (the same
/// <c>ExecuteWithWorkflowAsync</c> pattern <see cref="LocalInstanceQueryGateway"/> uses). Reads only the id; applies no
/// row security because it never returns instance content.
/// </summary>
public sealed class LocalInstanceIdResolver(IServiceScopeFactory serviceScopeFactory) : IInstanceIdResolver
{
    /// <inheritdoc />
    public Task<Result<Guid>> ResolveIdAsync(
        string domain,
        string workflow,
        string identifier,
        CancellationToken cancellationToken = default) =>
        serviceScopeFactory.ExecuteWithWorkflowAsync<Guid>(domain, workflow, null,
            async (sp, ct) =>
            {
                var repository = sp.GetRequiredService<IInstanceRepository>();
                var instance = await repository.FindByIdentifierAsReadOnlyAsync(identifier, ct);
                return instance is null
                    ? Result<Guid>.Fail(Error.NotFound(
                        WorkflowErrorCodes.InstanceNotFound,
                        $"Instance '{identifier}' was not found"))
                    : Result<Guid>.Ok(instance.Id);
            }, cancellationToken);
}
