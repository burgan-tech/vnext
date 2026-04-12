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
        return await _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var commandService = sp.GetRequiredService<IInstanceCommandAppService>();
                var unitOfWorkManager = sp.GetRequiredService<IUnitOfWorkManager>();

                await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, ct);
                
                var result =  await commandService.StartAsync(input, ct);

                await uow.CommitAsync(ct);
                return result;
            }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<StartInstanceOutput>> StartSubAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return await _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var commandService = sp.GetRequiredService<IInstanceCommandAppService>();
                var unitOfWorkManager = sp.GetRequiredService<IUnitOfWorkManager>();
                
                await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, ct);

                var result = await commandService.StartAsync(input, ct);
                await uow.CommitAsync(ct);
                return result;
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<TransitionOutput>> TransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, string.Empty,
            async (sp, ct) =>
            {
                var commandService = sp.GetRequiredService<IInstanceCommandAppService>();
                var unitOfWorkManager = sp.GetRequiredService<IUnitOfWorkManager>();

                await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, ct);
                
                var result =  await commandService.TransitionAsync(
                    instanceId.ToString(),
                    transitionKey,
                    input,
                    ct);

                await uow.CommitAsync(ct);
                return result;
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> CompleteAsync(
        FlowCompletedInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Flow, input.Version, async (sp, ct) =>
        {
            var subflowCompletionService = sp.GetRequiredService<ISubflowCompletionService>();
            await subflowCompletionService.CompletionAsync(input, ct);

            return Result.Ok();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> UpdateSubFlowStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Flow, input.Version, async (sp, ct) =>
        {
            var subflowStateService = sp.GetRequiredService<ISubflowStateService>();
            var unitOfWorkManager = sp.GetRequiredService<IUnitOfWorkManager>();

            await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew
            }, ct);

            await subflowStateService.UpdateParentStateAsync(input, ct);
            await uow.CommitAsync(ct);

            return Result.Ok();
        }, cancellationToken);
    }
}