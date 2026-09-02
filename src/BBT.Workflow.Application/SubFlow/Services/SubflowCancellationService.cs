using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowCancellationService" />
public sealed class SubflowCancellationService(
    IUnitOfWorkManager uowManager,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IWorkflowExecutionService workflowExecutionService,
    ITransitionLockScopeFactory transitionLockScopeFactory,
    ISubItemTerminalGuard terminalGuard,
    IDistributedEventBus eventBus,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<SubflowCancellationService> logger)
    : ISubflowCancellationService
{
    /// <summary>
    /// Attempt budget for re-publishing the terminal event from inside a correlation revert. See
    /// <c>SubflowCompletionService.MaxRearmAttempts</c> for the rationale.
    /// </summary>
    private const int MaxRearmAttempts = 5;

    private LockAcquireWait TerminalLockWait => executionOptions.Value.SubItemTerminalLockRetry.ToLockAcquireWait();

    /// <inheritdoc />
    public async Task CancellationAsync(
        SubItemCanceledInput input,
        CancellationToken cancellationToken = default)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Cancellation/{input.Domain}/{input.Flow}");
        SubFlowActivityHelper.EnrichWithCompletion(
            activity,
            input.SubInstanceId,
            input.InstanceId,
            input.Domain,
            input.Flow);
        activity?.SetTag(TelemetryConstants.TagNames.FlowVersion, input.Version ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.RootInstanceId, input.RootInstanceId?.ToString() ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, input.InstanceId.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.SubItemOutcome, SubItemTerminalOutcome.Canceled.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.TerminationOrigin, input.Termination?.Origin.ToString() ?? "legacy");
        activity?.SetTag(TelemetryConstants.TagNames.TerminationInitiator, input.Termination?.InitiatorInstanceId.ToString() ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.TerminationCascadeId, input.Termination?.CascadeId.ToString() ?? "N/A");
        var scopeProperties = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = input.Domain,
            [TelemetryConstants.TagNames.Flow] = input.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = input.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = input.InstanceId,
            [TelemetryConstants.TagNames.RootInstanceId] = input.RootInstanceId?.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.ParentInstanceId] = input.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = input.SubInstanceId,
            [TelemetryConstants.TagNames.SubItemType] = "N/A",
            [TelemetryConstants.TagNames.SubItemOutcome] = SubItemTerminalOutcome.Canceled.ToString(),
            [TelemetryConstants.TagNames.TerminationOrigin] = input.Termination?.Origin.ToString() ?? "legacy",
            [TelemetryConstants.TagNames.TerminationInitiator] = input.Termination?.InitiatorInstanceId.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.TerminationCascadeId] = input.Termination?.CascadeId.ToString() ?? "N/A"
        };
        using var logScope = logger.BeginScope(scopeProperties);

        Instance? parentInstance;
        Definitions.Workflow? parentWorkflow = null;

        // Per-subInstance terminal lock, independent of the main-flow lock and reserved keys:
        // a long-held chain lease never blocks the signal, parallel SubProcess terminal-closes
        // don't contend, and only duplicate deliveries of the SAME subInstance serialize.
        // Sync/async safe: distinct from every instance-status lock key (no self-deadlock).
        var lockKey = $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}";

        // Lock-free duplicate short-circuit — see SubflowCompletionService for the rationale.
        var probe = await terminalGuard.ProbeAsync(
            input.InstanceId,
            input.SubInstanceId,
            SubItemTerminalOutcome.Canceled,
            cancellationToken);

        if (probe != SubItemTerminalProbe.Proceed)
        {
            activity?.SetTag("vnext.subflow.result",
                probe == SubItemTerminalProbe.AlreadySettled
                    ? "correlation_already_settled_prelock"
                    : "terminal_outcome_conflict_prelock");
            SubFlowActivityHelper.SetSuccess(activity);
            return;
        }

        // Bounded wait rather than fail-fast: absorbs duplicates that collide with the original
        // while its transaction is still open.
        await using (var lockScope = await transitionLockScopeFactory.AcquireAsync(
                         lockKey, TerminalLockWait, cancellationToken))
        {
            if (!lockScope.IsAcquired)
            {
                logger.SubItemTerminalLockNotAcquired(lockKey, SubItemTerminalOutcome.Canceled.ToString());
                throw new SubflowTerminalLockNotAcquiredException(
                    input.Domain,
                    input.Flow,
                    input.InstanceId.ToString(),
                    SubItemTerminalOutcome.Canceled.ToString());
            }

            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
            parentInstance = await instanceRepository.FindWithAllCorrelationsAsync(
                input.InstanceId,
                cancellationToken);

            if (parentInstance == null)
            {
                logger.InstanceNotFound(input.InstanceId, input.Flow);
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

            activity?.SetTag(TelemetryConstants.TagNames.SubItemType, correlation.SubFlowType.Code);
            scopeProperties[TelemetryConstants.TagNames.SubItemType] = correlation.SubFlowType.Code;

            if (correlation.IsCompleted)
            {
                if (correlation.TerminalOutcome == SubItemTerminalOutcome.Canceled)
                {
                    logger.SubItemTerminalDuplicate(
                        SubItemTerminalOutcome.Canceled.ToString(),
                        input.InstanceId,
                        input.SubInstanceId);
                }
                else
                {
                    logger.SubItemTerminalConflict(
                        input.InstanceId,
                        input.SubInstanceId,
                        correlation.TerminalOutcome?.ToString() ?? "legacy",
                        SubItemTerminalOutcome.Canceled.ToString());
                }

                await uow.CommitAsync(cancellationToken);
                return;
            }

            // A terminal parent still needs an active SubProcess correlation closed, but
            // a blocking SubFlow must not mutate or resume an already-terminal parent.
            if (parentInstance.IsCompleted && correlation.SubFlowType.Equals(SubFlowType.SubFlow))
            {
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

            if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
            {
                correlation.MarkSettled(input.CanceledAt);
            }

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
            lockKey,
            cancellationToken);

        await MarkSettlementBestEffortAsync(
            input.SubInstanceId,
            input.CanceledAt,
            cancellationToken);
    }

    private async Task MarkSettlementBestEffortAsync(
        Guid subInstanceId,
        DateTime settledAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var markerUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
            await terminalGuard.TryMarkSettledAsync(
                subInstanceId,
                SubItemTerminalOutcome.Canceled,
                settledAt,
                cancellationToken);
            await markerUow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.SubItemSettlementMarkerWriteFailed(ex, nameof(SubItemTerminalOutcome.Canceled), subInstanceId);
        }
    }

    private async Task ResumeParentAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        SubItemCanceledInput input,
        string parentLockKey,
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
                await RevertCorrelationAsync(
                    parentLockKey,
                    parentInstance.Id,
                    input);
            }
            catch (Exception revertException)
            {
                logger.SubItemCorrelationRevertFailed(
                    revertException,
                    parentInstance.Id,
                    input.SubInstanceId);
            }

            throw;
        }
    }

    private async Task RevertCorrelationAsync(
        string parentLockKey,
        Guid parentInstanceId,
        SubItemCanceledInput originalInput)
    {
        var subInstanceId = originalInput.SubInstanceId;
        var cancellationToken = CancellationToken.None;
        await using var lockScope = await transitionLockScopeFactory.AcquireAsync(parentLockKey, cancellationToken);
        if (!lockScope.IsAcquired)
        {
            throw new InvalidOperationException(
                $"Parent instance compensation lock '{parentLockKey}' could not be acquired.");
        }

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        var parentInstance = await instanceRepository.FindWithAllCorrelationsAsync(
                                 parentInstanceId,
                                 cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Parent instance {parentInstanceId} was not found while reverting cancellation");

        if (parentInstance.IsCompleted)
        {
            await uow.CommitAsync(cancellationToken);
            return;
        }

        if (parentInstance.RevertCorrelation(subInstanceId) != null)
        {
            await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
        }

        // See SubflowCompletionService.RevertCorrelationInNewUowAsync for the rationale: the
        // durable backup may already be consumed by the lock-free duplicate ACK, so re-publish
        // the terminal event in THIS UoW — the outbox row commits atomically with the revert.
        var rearmAttempt = originalInput.RearmAttempt ?? 0;
        if (rearmAttempt >= MaxRearmAttempts)
        {
            logger.SubflowTerminalRearmExhausted(parentInstanceId, subInstanceId, rearmAttempt);
        }
        else
        {
            var rearmEvent = BuildTerminalEventFromInput(originalInput, rearmAttempt + 1);
            await eventBus.PublishAsync(rearmEvent, cancellationToken: cancellationToken);
            logger.SubflowTerminalRearmed(parentInstanceId, subInstanceId, rearmAttempt + 1);
        }

        await uow.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Reconstructs the terminal event from the service's own input DTO — the exact inverse of
    /// <c>SubflowTerminalRelay.MapToSubItemCanceledInput</c> (verbatim field copy) — so a re-arm
    /// publishes the same fact the original delivery carried.
    /// </summary>
    /// <remarks>
    /// <see cref="SubItemType.SubFlow"/> is hardcoded: only a blocking SubFlow reaches
    /// <see cref="ResumeParentAsync"/> (a SubProcess correlation returns before this point in
    /// <see cref="CancellationAsync"/>), so this is the only kind the revert path ever sees. The
    /// input carries no <c>SubItemType</c> field to round-trip because of that invariant.
    /// </remarks>
    private static InstanceSubCanceledEvent BuildTerminalEventFromInput(
        SubItemCanceledInput input,
        int rearmAttempt)
    {
        var termination = input.Termination ?? TerminationContext.Direct(input.InstanceId);
        return new InstanceSubCanceledEvent
        {
            InstanceId = input.InstanceId,
            SubInstanceId = input.SubInstanceId,
            Domain = input.Domain,
            Flow = input.Flow,
            Version = input.Version,
            CanceledState = input.CanceledState,
            CanceledAt = input.CanceledAt,
            RootInstanceId = input.RootInstanceId,
            SubItemType = SubItemType.SubFlow,
            Sync = input.Sync,
            TerminationOrigin = termination.Origin,
            InitiatorInstanceId = termination.InitiatorInstanceId,
            CascadeId = termination.CascadeId,
            TraceRoot = input.TraceRoot,
            ParentTraceRoot = input.ParentTraceRoot,
            RearmAttempt = rearmAttempt
        };
    }
}
