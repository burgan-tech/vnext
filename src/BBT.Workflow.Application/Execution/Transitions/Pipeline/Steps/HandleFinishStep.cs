using BBT.Aether.Clock;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow.Headers;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that handles workflow finishing.
/// Manages workflow completion when the target state type is Finish.
/// This step runs after all other pipeline steps to ensure proper completion.
/// Uses Result pattern for exception-free error handling.
/// SubItem completion events are now published as domain events via Instance.Complete().
///
/// It is also where a terminal instance hands back its distributed resource locks. That release is
/// armed on the ambient unit of work's OnCompleted, so it runs only once the terminal state is
/// COMMITTED: releasing before the commit would free the lock for a transition that may still roll
/// back, letting a second instance into the section the lock exists to serialize. The event-driven
/// cleanup path (InstanceCancellationService) stays as an idempotent backstop for the terminal paths
/// that never reach this step.
/// </summary>
/// <param name="instanceRepository">Persists the finished instance.</param>
/// <param name="logger">Step logger.</param>
/// <param name="subItemDataResolver">Opens the data a sub-item hands its parent; null uses the stored row.</param>
/// <param name="instanceDataWriteService">Flushes buffered data for <c>history: none</c> flows.</param>
/// <param name="carriedHeaderPolicy">Filters the request headers that ride the terminal event to the parent; null disables header capture, production DI always supplies it.</param>
/// <param name="agentSessionStore">Deletes the agent sessions (<c>i:*</c>) a finishing instance owns; null skips it.</param>
public sealed class HandleFinishStep(
    IInstanceRepository instanceRepository,
    ILogger<HandleFinishStep> logger,
    ISubItemEventDataResolver? subItemDataResolver = null,
    IInstanceDataWriteService? instanceDataWriteService = null,
    IResourceLockService? resourceLockService = null,
    IUnitOfWorkManager? uowManager = null,
    IClock? clock = null,
    ICarriedHeaderPolicy? carriedHeaderPolicy = null,
    BBT.Workflow.Instances.Agents.IAgentSessionStore? agentSessionStore = null) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Finish;
    
    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        
        // Check applicability - skip if not finish scenario
        if (!IsFinishScenario(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // A completing sub item hands its data to the parent, opened with its own key (the parent cannot open it).
        var subItemData = context.IsCancelTransition()
            ? null
            : await subItemDataResolver.ResolveOrStoredAsync(context.Instance, cancellationToken);

        // history: none (vnext#1006): the buffered data is written here, once, in the same
        // transaction as the completion — a Completed instance never commits without its data.
        if (context.Instance.IsDataBuffered && instanceDataWriteService is not null)
        {
            await instanceDataWriteService.FlushAsync(
                context.Instance,
                context.Workflow,
                async ct =>
                {
                    UpdateInstanceStatus(context, subItemData);
                    context.ExtractAndDeferInstanceEvents();
                    await instanceRepository.UpdateAsync(context.Instance, true, ct);
                    await DeleteAgentSessionsAsync(context, ct);
                },
                cancellationToken);
            context.Items["IsFinishState"] = true;
            await ArmResourceLockReleaseAsync(context, cancellationToken);
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Update status -> Extract events -> Persist -> Drop agent sessions -> Mark finish
        return await Result.Ok(context)
            .Tap(ctx => UpdateInstanceStatus(ctx, subItemData))
            .Tap(ctx => ctx.ExtractAndDeferInstanceEvents())
            .TapAsync(ctx => instanceRepository.UpdateAsync(ctx.Instance, true, cancellationToken))
            .TapAsync(ctx => DeleteAgentSessionsAsync(ctx, cancellationToken))
            .Tap(ctx => ctx.Items["IsFinishState"] = true)
            .TapAsync(ctx => ArmResourceLockReleaseAsync(ctx, cancellationToken))
            .Map(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// A completed or cancelled instance has no later agent entry, so the conversations it owns
    /// (<c>memory.mode</c> <c>state</c> / <c>flow</c>, and approval holds) are deleted in the same
    /// transaction. Shared <c>k:*</c> sessions outlive it and expire by <c>retentionDays</c>. A flow
    /// without an AiAgent state issues no statement.
    /// </summary>
    private async Task DeleteAgentSessionsAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        if (agentSessionStore is null || !context.Workflow.HasAiAgentState)
            return;

        await agentSessionStore.DeleteInstanceSessionsAsync(context.Instance.Id, cancellationToken);
    }

    /// <summary>
    /// Hands back every distributed lock this instance holds, once the terminal state is committed.
    ///
    /// Timing is the whole point. Freeing the lock inline would publish "the section is free" while the
    /// transaction can still roll back; waiting for the cleanup EVENT (outbox to Dapr to inbox) frees it
    /// only as fast as that chain drains, and a chain that stalls leaves the lock to its TTL — every
    /// queued instance then burns its retry budget against a holder that finished minutes ago. On the
    /// ambient unit of work's OnCompleted it is neither: the commit has landed and the release is still
    /// in this request.
    ///
    /// Best-effort by design: the release is idempotent, the TTL is the ultimate safety net, and a lock
    /// that cannot be handed back must never fail a business transition that already committed.
    /// </summary>
    private async Task ArmResourceLockReleaseAsync(
        TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        if (resourceLockService is null)
            return;

        var lockKeys = context.Instance.GetTrackedResourceLocks();
        if (lockKeys.Count == 0)
            return;

        var instanceId = context.Instance.Id;
        var owner = instanceId.ToString();
        var held = lockKeys
            .Select(key => (Key: key, AcquiredAt: context.Instance.GetResourceLockAcquiredAt(key)))
            .ToList();
        var domain = context.Domain;
        var flowKey = context.Workflow.Key;

        var uow = uowManager?.Current;
        if (uow is null)
        {
            // No ambient unit of work: the repository update above stands on its own, so there is no
            // commit left to wait for. AWAITED on purpose — IResourceLockService is scoped, and letting
            // the release outlive this step would hand it a DaprClient from a scope that may already be
            // disposed. The latency is one release call on a path that is already finishing.
            await ReleaseAllAsync(held, owner, instanceId, domain, flowKey, cancellationToken);
            return;
        }

        // OnCompleted runs while the unit of work — and therefore the scope that owns the lock service
        // — is still alive.
        uow.OnCompleted(async _ =>
            await ReleaseAllAsync(held, owner, instanceId, domain, flowKey, CancellationToken.None));
    }

    private async Task ReleaseAllAsync(
        IReadOnlyList<(string Key, DateTime? AcquiredAt)> held,
        string owner,
        Guid instanceId,
        string domain,
        string flowKey,
        CancellationToken cancellationToken)
    {
        foreach (var (lockKey, acquiredAt) in held)
        {
            try
            {
                await resourceLockService!.ReleaseAsync(lockKey, owner, cancellationToken);

                // Metered only when the acquisition was stamped: an instance started before the stamp
                // existed would otherwise report its whole age as the hold time and poison the signal.
                if (acquiredAt is { } start)
                {
                    WorkflowMetrics.ResourceLockHoldDurationMs.Record(
                        ((clock?.UtcNow ?? DateTime.UtcNow) - start).TotalMilliseconds,
                        new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Domain, domain),
                        new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Flow, flowKey));
                }
            }
            catch (Exception ex)
            {
                logger.ResourceLockAutoReleaseError(ex, lockKey, instanceId);
            }
        }
    }

    /// <summary>
    /// Determines if this is a finish scenario (cancel, exit, or finish state).
    /// </summary>
    private bool IsFinishScenario(TransitionExecutionContext context)
    {
        var isCancelTransition = context.IsCancelTransition();
        var isExitTransition = context.IsExitTransition();
        
        // Cancel and Exit transitions always go through finish
        if (isCancelTransition || isExitTransition)
        {
            return true;
        }

        // Check for null target or non-finish state
        if (context.Target == null)
        {
            logger.TargetStateNull(context.InstanceId);
            return false;
        }

        return context.Target.StateType == StateType.Finish;
    }

    /// <summary>
    /// Updates instance status based on transition type.
    /// </summary>
    private void UpdateInstanceStatus(TransitionExecutionContext context, System.Text.Json.JsonElement? subItemData)
    {
        // The headers of the request that settles this child ride the terminal event to the parent;
        // a root instance has no parent to carry them to.
        var carriedHeaders = context.Instance.IsSubItem ? carriedHeaderPolicy?.Capture(context.Headers) : null;
        if (context.IsCancelTransition())
        {
            logger.InstanceCanceling(context.Instance.Id);
            context.Instance.Cancel(
                context.Domain,
                context.CallerMode == ExecMode.Sync,
                context.Termination,
                carriedHeaders: carriedHeaders);
        }
        else
        {
            logger.InstanceCompleting(context.Instance.Id);
            context.Instance.Complete(context.Domain, context.CallerMode == ExecMode.Sync, subItemData,
                carriedHeaders: carriedHeaders);
        }
    }
}
