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
    public async Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
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
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var authorizeAppService = scope.ServiceProvider.GetRequiredService<IAuthorizeAppService>();

        using (currentSchema.Use(workflow))
        {
            return await authorizeAppService.GetAuthorizeResultForInstanceAsync(
                domain, workflow, instanceId, role, transitionKey, functionKey, version, checkQueryRoles, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var authorizeAppService = scope.ServiceProvider.GetRequiredService<IAuthorizeAppService>();

        using (currentSchema.Use(workflow))
        {
            return await authorizeAppService.GetAuthorizationMatrixForInstanceAsync(
                domain, workflow, instanceId, version, cancellationToken);
        }
    }
}
