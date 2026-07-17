using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowCancellationService" />
public sealed class SubflowCancellationService(
    IUnitOfWorkManager uowManager,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IWorkflowExecutionService workflowExecutionService,
    ILogger<SubflowCancellationService> logger)
    : ISubflowCancellationService
{
    /// <inheritdoc />
    public async Task CancellationAsync(
        SubItemCanceledInput input,
        CancellationToken cancellationToken = default)
    {
        Instance? parentInstance;
        Definitions.Workflow? parentWorkflow = null;

        await using (var uow = uowManager.Begin(
                         new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }))
        {
            parentInstance = await instanceRepository.FindAsync(
                input.InstanceId,
                true,
                cancellationToken);

            if (parentInstance == null)
            {
                logger.InstanceNotFound(input.InstanceId, input.Flow);
                await uow.CommitAsync(cancellationToken);
                return;
            }

            if (parentInstance.Status.Equals(InstanceStatus.Completed) ||
                parentInstance.Status.Equals(InstanceStatus.Faulted))
            {
                await uow.CommitAsync(cancellationToken);
                return;
            }

            var correlation = parentInstance.FindCorrelationBySubInstanceId(input.SubInstanceId);
            if (correlation == null)
            {
                logger.SubFlowCorrelationNotFound(input.SubInstanceId);
                await uow.CommitAsync(cancellationToken);
                return;
            }

            if (correlation.IsCompleted)
            {
                if (correlation.TerminalOutcome == SubItemTerminalOutcome.Canceled)
                {
                    logger.LogDebug(
                        "Duplicate canceled SubItem outcome for parent {ParentInstanceId}, child {SubInstanceId}",
                        input.InstanceId,
                        input.SubInstanceId);
                }
                else
                {
                    logger.LogWarning(
                        "SubItem terminal outcome conflict for parent {ParentInstanceId}, child {SubInstanceId}: existing {ExistingOutcome}, incoming {IncomingOutcome}",
                        input.InstanceId,
                        input.SubInstanceId,
                        correlation.TerminalOutcome?.ToString() ?? "legacy",
                        SubItemTerminalOutcome.Canceled);
                }

                await uow.CommitAsync(cancellationToken);
                return;
            }

            if (correlation.SubFlowType.Equals(SubFlowType.SubFlow))
            {
                var workflowResult = await componentCacheStore.GetFlowAsync(
                    input.Domain,
                    input.Flow,
                    input.Version,
                    cancellationToken);

                if (!workflowResult.IsSuccess)
                {
                    throw new SubflowCompletionException(
                        input.Domain,
                        input.Flow,
                        input.InstanceId.ToString(),
                        workflowResult.Error.Code,
                        workflowResult.Error.Message ?? "Parent workflow could not be loaded");
                }

                parentWorkflow = workflowResult.Value!;
            }

            correlation.UpdateSubFlowState(input.CanceledState, input.CanceledAt);
            parentInstance.CompleteCorrelation(
                input.SubInstanceId,
                SubItemTerminalOutcome.Canceled,
                input.CanceledAt);

            await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
            await uow.CommitAsync(cancellationToken);

            if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
            {
                return;
            }
        }

        await ResumeParentAsync(
            parentInstance,
            parentWorkflow!,
            input,
            cancellationToken);
    }

    private async Task ResumeParentAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        SubItemCanceledInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = new WorkflowExecutionContext
            {
                Domain = parentWorkflow.Domain,
                WorkflowKey = parentWorkflow.Key,
                WorkflowVersion = parentWorkflow.Version,
                InstanceId = parentInstance.Id.ToString(),
                TransitionKey = string.Empty,
                TriggerType = TriggerType.Automatic,
                Mode = ExecMode.Resume,
                CallerMode = input.Sync ? ExecMode.Sync : ExecMode.Async,
                Actor = ExecutionActor.System,
                RequestedAt = DateTimeOffset.UtcNow,
                Execution = new ExecutionInfo
                {
                    ExecutionChainId = Guid.NewGuid().ToString("N"),
                    ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep,
                    IsSubFlowResume = true,
                    SubFlowResumeInstanceId = input.SubInstanceId
                }
            };

            var result = await workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);
            var isSoftFailure = !result.IsSuccess &&
                                (result.Error.Code == WorkflowErrorCodes.AutoTransitionConditionNotMet ||
                                 result.Error.Code == WorkflowErrorCodes.InstanceCompleted);

            if (!result.IsSuccess && !isSoftFailure)
            {
                throw new SubflowCompletionException(
                    parentWorkflow.Domain,
                    parentWorkflow.Key,
                    parentInstance.Id.ToString(),
                    result.Error.Code,
                    result.Error.Message ?? "Unknown error");
            }
        }
        catch
        {
            try
            {
                await RevertCorrelationAsync(parentInstance.Id, input.SubInstanceId, cancellationToken);
            }
            catch (Exception revertException)
            {
                logger.LogError(
                    revertException,
                    "Failed to revert canceled SubItem correlation for parent {ParentInstanceId}, child {SubInstanceId}",
                    parentInstance.Id,
                    input.SubInstanceId);
            }

            throw;
        }
    }

    private async Task RevertCorrelationAsync(
        Guid parentInstanceId,
        Guid subInstanceId,
        CancellationToken cancellationToken)
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        var parentInstance = await instanceRepository.FindWithAllCorrelationsAsync(
                                 parentInstanceId,
                                 cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Parent instance {parentInstanceId} was not found while reverting cancellation");

        if (parentInstance.RevertCorrelation(subInstanceId) != null)
        {
            await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
        }

        await uow.CommitAsync(cancellationToken);
    }
}
