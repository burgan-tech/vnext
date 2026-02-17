using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routed implementation of authorize gateway.
/// Routes between local and remote execution based on target domain.
/// Uses IRuntimeInfoProvider.IsDomainMatch() to determine if target domain is local.
/// </summary>
public sealed class RoutedAuthorizeGateway : IAuthorizeGateway
{
    private readonly LocalAuthorizeGateway _local;
    private readonly RemoteAuthorizeGateway _remote;
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;

    /// <summary>
    /// Initializes a new instance of RoutedAuthorizeGateway.
    /// </summary>
    /// <param name="local">The local gateway for same-domain execution.</param>
    /// <param name="remote">The remote gateway for cross-domain execution.</param>
    /// <param name="runtimeInfoProvider">Provider for runtime domain information.</param>
    public RoutedAuthorizeGateway(
        LocalAuthorizeGateway local,
        RemoteAuthorizeGateway remote,
        IRuntimeInfoProvider runtimeInfoProvider)
    {
        _local = local;
        _remote = remote;
        _runtimeInfoProvider = runtimeInfoProvider;
    }

    /// <inheritdoc />
    public Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version,
        bool checkQueryRoles,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(domain)
            ? _local.GetAuthorizeResultForInstanceAsync(domain, workflow, instanceId, role, transitionKey, functionKey, version, checkQueryRoles, cancellationToken)
            : _remote.GetAuthorizeResultForInstanceAsync(domain, workflow, instanceId, role, transitionKey, functionKey, version, checkQueryRoles, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return _runtimeInfoProvider.IsDomainMatch(domain)
            ? _local.GetAuthorizationMatrixForInstanceAsync(domain, workflow, instanceId, version, cancellationToken)
            : _remote.GetAuthorizationMatrixForInstanceAsync(domain, workflow, instanceId, version, cancellationToken);
    }
}
