using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization.Remote;

/// <summary>
/// Remote service interface for authorize operations.
/// Acts as a client to the authorize instance function endpoint for remote workflow instances.
/// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/authorize
/// </summary>
public interface IRemoteAuthorizeAppService
{
    /// <summary>
    /// Returns whether the given role is allowed for the requested transition, function, or query roles for the specified instance by calling the remote API.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow.</param>
    /// <param name="instanceId">Target instance identifier.</param>
    /// <param name="role">Role to check.</param>
    /// <param name="transitionKey">Optional transition key.</param>
    /// <param name="functionKey">Optional function key.</param>
    /// <param name="version">Optional component version.</param>
    /// <param name="checkQueryRoles">When true, evaluates state-based query roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the authorize output or an error.</returns>
    Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version,
        bool checkQueryRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authorization matrix for the specified instance by calling the remote API.
    /// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/permissions
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow.</param>
    /// <param name="instanceId">Target instance identifier.</param>
    /// <param name="version">Optional workflow version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the authorization matrix output or an error.</returns>
    Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default);
}
