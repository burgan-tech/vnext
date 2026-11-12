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
    /// Exactly one of transitionKey, functionKey, checkQueryRoles or checkAck must be provided.
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
        bool checkAck = false,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the caller may start an instance of the workflow: the workflow's own
    /// <c>startTransition</c> grants are evaluated without an instance. Lets a gateway gate
    /// <c>POST .../instances/start</c>, where there is no instance to address yet.
    /// Instance-bound grants (<c>$InstanceStarter</c>, <c>$PreviousUser</c>, the behalf-of pair,
    /// <c>$user.</c> / <c>$userBehalfOf.</c> / <c>$role.</c> context references) have nothing to resolve
    /// against and never match: they neither allow nor deny. No grants → allowed.
    /// </summary>
    Task<Result<AuthorizeOutput>> GetStartAuthorizeResultAsync(
        string domain,
        string workflow,
        string? role,
        string? version = null,
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
