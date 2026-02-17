using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Gateway interface for authorize operations.
/// Routes between local and remote execution based on target domain.
/// When target domain matches the current runtime, executes locally via IAuthorizeAppService.
/// When target domain differs, delegates to remote HTTP service.
/// </summary>
public interface IAuthorizeGateway
{
    /// <summary>
    /// Returns whether the given role is allowed for the requested transition, function, or query roles for the specified instance.
    /// Routes to local or remote based on target domain.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow.</param>
    /// <param name="instanceId">Target instance identifier.</param>
    /// <param name="role">Role to check.</param>
    /// <param name="transitionKey">Optional transition key (mutually exclusive with functionKey and checkQueryRoles).</param>
    /// <param name="functionKey">Optional function key (mutually exclusive with transitionKey and checkQueryRoles).</param>
    /// <param name="version">Optional component version. Empty/null = latest.</param>
    /// <param name="checkQueryRoles">When true, evaluates state-based query roles (instance only).</param>
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authorization matrix for the specified instance context. Routes to local or remote based on target domain.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow.</param>
    /// <param name="instanceId">Target instance identifier.</param>
    /// <param name="version">Optional workflow version. Empty/null = latest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing the authorization matrix output or an error.</returns>
    Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default);
}
