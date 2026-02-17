using BBT.Aether.Application;
using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Application service for authorize and authorization matrix system functions.
/// </summary>
public interface IAuthorizeAppService : IApplicationService
{
    /// <summary>
    /// Returns whether the given role is allowed for the requested transition or function (workflow-level).
    /// Exactly one of transitionKey or functionKey must be provided. If checkQueryRoles is true, returns validation error (query roles only valid at instance level).
    /// </summary>
    /// <param name="version">Optional component version (workflow version when transitionKey, function version when functionKey). Empty/null = latest.</param>
    /// <param name="checkQueryRoles">When true, returns error; query roles check is only valid for instance-level authorize.</param>
    Task<Result<AuthorizeOutput>> GetAuthorizeResultAsync(
        string domain,
        string workflow,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version = null,
        bool checkQueryRoles = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the given role is allowed for the requested transition, function, or state-based query roles for the specified instance.
    /// Exactly one of transitionKey, functionKey, or checkQueryRoles must be provided.
    /// </summary>
    /// <param name="version">Optional component version. Empty/null = latest.</param>
    /// <param name="checkQueryRoles">When true, evaluates state-based query roles (instance current state); mutually exclusive with transitionKey and functionKey.</param>
    Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version = null,
        bool checkQueryRoles = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authorization matrix for the workflow (queryRoles, states, transitions, functions).
    /// </summary>
    /// <param name="version">Optional workflow version. Empty/null = latest.</param>
    Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixAsync(
        string domain,
        string workflow,
        string? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authorization matrix in instance context. When instance has active subflow, returns subflow's matrix; otherwise returns workflow's matrix.
    /// </summary>
    /// <param name="version">Optional workflow version. Empty/null = latest.</param>
    Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version = null,
        CancellationToken cancellationToken = default);
}
