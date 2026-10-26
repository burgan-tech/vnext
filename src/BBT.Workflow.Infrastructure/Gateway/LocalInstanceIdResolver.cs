using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Resolves an instance key to its id through the repository in the target flow's schema scope (the same
/// <c>ExecuteWithWorkflowAsync</c> pattern <see cref="LocalInstanceQueryGateway"/> uses). Loads the read-only instance and
/// returns only its id; applies no row security because it never returns instance content. Runs as the target
/// domain through the co-hosted database boundary, exactly as <see cref="RoutedInstanceQueryGateway"/> ran the
/// key lookup before, so a co-hosted domain in its own database is read from that database.
/// </summary>
public sealed class LocalInstanceIdResolver(
    IServiceScopeFactory serviceScopeFactory,
    ICoHostedDatabaseBoundary? databaseBoundary = null) : IInstanceIdResolver
{
    /// <inheritdoc />
    public Task<Result<Guid>> ResolveIdAsync(
        string domain,
        string workflow,
        string identifier,
        CancellationToken cancellationToken = default) =>
        LocalDomainCall.Run(databaseBoundary, domain, () =>
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
                }, cancellationToken));
}
