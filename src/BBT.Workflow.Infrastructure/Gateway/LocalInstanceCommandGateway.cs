using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Local implementation of instance command gateway.
/// Executes commands locally with proper schema context and unit of work management.
/// Uses IServiceScopeFactory to create fresh scope for each operation ensuring clean transaction state.
/// </summary>
public sealed class LocalInstanceCommandGateway : IInstanceCommandGateway
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of LocalInstanceCommandGateway.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory for creating service scopes.</param>
    public LocalInstanceCommandGateway(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public async Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return await _serviceScopeFactory.ExecuteWithWorkflowAsync<StartInstanceOutput>(input.Domain, input.Workflow, input.Version, async (sp, cancellationToken) =>
        {
            var currentSchema = sp.GetRequiredService<ICurrentSchema>();
            var commandService = sp.GetRequiredService<IInstanceCommandAppService>();

            using (currentSchema.Use(input.Workflow))
            {
                return await commandService.StartAsync(input, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        // StartSubAsync uses the same StartAsync endpoint but with sub-specific handling
        // The IInstanceCommandAppService.StartAsync handles both normal and sub-flow starts
        return await _serviceScopeFactory.ExecuteWithWorkflowAsync<StartInstanceOutput>(input.Domain, input.Workflow, input.Version, async (sp, cancellationToken) =>
        {
            var currentSchema = sp.GetRequiredService<ICurrentSchema>();
            var commandService = sp.GetRequiredService<IInstanceCommandAppService>();

            using (currentSchema.Use(input.Workflow))
            {
                return await commandService.StartAsync(input, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var commandService = scope.ServiceProvider.GetRequiredService<IInstanceCommandAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            return await commandService.TransitionAsync(
                instanceId.ToString(),
                transitionKey,
                input,
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var subflowCompletionService = scope.ServiceProvider.GetRequiredService<ISubflowCompletionService>();
        var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using (currentSchema.Use(input.Flow))
        {
            await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew
            }, cancellationToken);

            await subflowCompletionService.CompletionAsync(input, cancellationToken);
            await uow.SaveChangesAsync(cancellationToken);
            await uow.CommitAsync(cancellationToken);

            return Result.Ok();
        }
    }

    /// <inheritdoc />
    public async Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var subflowStateService = scope.ServiceProvider.GetRequiredService<ISubflowStateService>();
        var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using (currentSchema.Use(input.Flow))
        {
            await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew
            }, cancellationToken);

            await subflowStateService.UpdateParentStateAsync(input, cancellationToken);
            await uow.SaveChangesAsync(cancellationToken);
            await uow.CommitAsync(cancellationToken);

            return Result.Ok();
        }
    }
}

