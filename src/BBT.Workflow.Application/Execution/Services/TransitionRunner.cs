using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Orchestrates transition execution with isolated DI scope and UoW.
/// Transition chaining (auto/scheduled) is now handled by TransitionPipeline via sync dispatch.
/// This runner focuses on UoW lifecycle management for a single transition execution.
/// Uses ExecuteWithWorkflowAsync extension for automatic workflow loading and context management.
/// </summary>
public sealed class TransitionRunner(
    IServiceScopeFactory scopeFactory) : ITransitionRunner
{
    /// <inheritdoc />
    /// <summary>
    /// Runs a transition in its own DI scope + RequiresNew UoW.
    /// Sync dispatch chain for auto transitions is managed by TransitionPipeline.
    /// </summary>
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // Execute in isolated scope + UoW
        var hopResult = await ExecuteWithScopeAsync(context, cancellationToken);
        if (!hopResult.IsSuccess)
            return Result<TransitionOutput>.Fail(hopResult.Error);

        var coreOutput = hopResult.Value!;
        return Result<TransitionOutput>.Ok(coreOutput.Output);
    }

    /// <summary>
    /// Executes the transition in a new DI scope with RequiresNew UoW.
    /// This ensures complete isolation from any ambient UoW.
    /// Uses ExecuteWithWorkflowAsync extension for automatic workflow loading and IWorkflowContext setup.
    /// </summary>
    private Task<Result<TransitionCoreOutput>> ExecuteWithScopeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        return scopeFactory.ExecuteWithWorkflowAsync(context.Domain, context.WorkflowKey, context.WorkflowVersion, async (sp, cancellationToken) =>
        {
            var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
            var core = sp.GetRequiredService<IWorkflowExecutionCore>();
            var currentUser = sp.GetRequiredService<ICurrentUser>();

            using (currentUser.ChangeFromHeaders(context.Headers))
            {
                await using var uow = await uowManager.BeginAsync(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
                    cancellationToken);

                var coreResult = await core.ExecuteTransitionCoreAsync(context, cancellationToken);
                if (!coreResult.IsSuccess)
                    return Result<TransitionCoreOutput>.Fail(coreResult.Error);

                // Commit is THE boundary
                await uow.CommitAsync(cancellationToken);

                return coreResult;
            }
        }, cancellationToken);
    }
}

