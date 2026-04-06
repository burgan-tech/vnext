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
    /// Returns whether the given role is allowed for the requested transition, function, or state-based query roles for the specified instance.
    /// Exactly one of transitionKey, functionKey, or checkQueryRoles must be provided.
    /// </summary>
    Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version = null,
        bool checkQueryRoles = false,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the authorization matrix in instance context. When instance has active subflow, returns subflow's matrix; otherwise returns workflow's matrix.
    /// </summary>
    Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version = null,
        CancellationToken cancellationToken = default);
}
