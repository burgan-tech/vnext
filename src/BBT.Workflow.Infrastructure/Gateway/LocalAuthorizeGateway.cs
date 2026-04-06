using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Local implementation of authorize gateway.
/// Executes authorize locally via IAuthorizeAppService with proper schema context.
/// </summary>
public sealed class LocalAuthorizeGateway : IAuthorizeGateway
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of LocalAuthorizeGateway.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory for creating service scopes.</param>
    public LocalAuthorizeGateway(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
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
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
        {
            var currentSchema = sp.GetRequiredService<ICurrentSchema>();
            var authorizeAppService = sp.GetRequiredService<IAuthorizeAppService>();

            using (currentSchema.Use(workflow))
            {
                return await authorizeAppService.GetAuthorizeResultForInstanceAsync(
                    domain, workflow, instanceId, role, transitionKey, functionKey, version, checkQueryRoles, requestContext, ct);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
        {
            var currentSchema = sp.GetRequiredService<ICurrentSchema>();
            var authorizeAppService = sp.GetRequiredService<IAuthorizeAppService>();

            using (currentSchema.Use(workflow))
            {
                return await authorizeAppService.GetAuthorizationMatrixForInstanceAsync(
                    domain, workflow, instanceId, version, ct);
            }
        }, cancellationToken);
    }
}
