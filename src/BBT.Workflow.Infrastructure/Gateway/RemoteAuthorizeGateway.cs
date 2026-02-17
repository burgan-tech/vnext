using BBT.Aether.Results;
using BBT.Workflow.Authorization.Remote;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Remote implementation of authorize gateway.
/// Delegates all operations to IRemoteAuthorizeAppService for HTTP-based execution.
/// Used when target domain differs from the current runtime domain.
/// </summary>
public sealed class RemoteAuthorizeGateway : IAuthorizeGateway
{
    private readonly IRemoteAuthorizeAppService _remoteService;

    /// <summary>
    /// Initializes a new instance of RemoteAuthorizeGateway.
    /// </summary>
    /// <param name="remoteService">The remote authorize service for HTTP calls.</param>
    public RemoteAuthorizeGateway(IRemoteAuthorizeAppService remoteService)
    {
        _remoteService = remoteService;
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
        return _remoteService.GetAuthorizeResultForInstanceAsync(
            domain, workflow, instanceId, role, transitionKey, functionKey, version, checkQueryRoles, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return _remoteService.GetAuthorizationMatrixForInstanceAsync(
            domain, workflow, instanceId, version, cancellationToken);
    }
}
