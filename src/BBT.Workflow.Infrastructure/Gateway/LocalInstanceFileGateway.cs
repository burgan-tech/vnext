using BBT.Aether.Results;
using BBT.Workflow.Files;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads an x-storage file of an instance in this runtime's domain, service-to-service (no authorization).
/// Establishes the schema scope with <c>ExecuteWithWorkflowAsync</c> — the same pattern
/// <see cref="LocalRelatedInstanceReader"/> uses; the version is left to resolve to the latest flow definition
/// only for the scope, while <see cref="InstanceFileAppService"/> loads the instance's own pinned version.
/// </summary>
public sealed class LocalInstanceFileGateway(IServiceScopeFactory serviceScopeFactory) : IInstanceFileGateway
{
    /// <inheritdoc />
    public Task<Result<InstanceFileContent>> ReadAsync(
        string domain, string flow, string instance, string file, CancellationToken cancellationToken) =>
        serviceScopeFactory.ExecuteWithWorkflowAsync(
            domain,
            flow,
            null,
            async (serviceProvider, ct) =>
            {
                var service = serviceProvider.GetRequiredService<IInstanceFileAppService>();
                return await service.ReadAsync(
                    new InstanceFileRequest(domain, flow, instance, file, IfNoneMatch: null, Authorization: null), ct);
            },
            cancellationToken);
}
