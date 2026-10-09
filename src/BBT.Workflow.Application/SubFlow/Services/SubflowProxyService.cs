using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc />
/// <remarks>
/// Replaces, for every request that enters through <c>InstanceCommandAppService.TransitionAsync</c>,
/// the accept-time chain reserve + parent job + <c>ForwardToSubflowJob</c> relay: the parent never
/// owns anything for a forwarded request, so there is nothing to reserve, compensate or settle on it.
/// Each intermediate level runs the same check in its own <c>TransitionAsync</c>, so the request
/// reaches the leaf level by level, and the leaf alone performs admission.
/// </remarks>
public sealed class SubflowProxyService(
    ITransitionAdmissionService transitionAdmissionService,
    ISubflowForwardingService subflowForwardingService,
    IInstanceRepository instanceRepository,
    IUnitOfWorkManager unitOfWorkManager,
    ILogger<SubflowProxyService> logger) : ISubflowProxyService
{
    /// <inheritdoc />
    public async Task<Result<TransitionOutput>?> TryProxyAsync(
        InstanceExecutionSnapshot snapshot,
        Definitions.Workflow workflow,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken)
    {
        if (snapshot.ActiveSubFlow is not { } child)
            return null;

        // An older-version parent relays with the chain-reserve claim: its accept already flipped
        // this chain down to the leaf, and only the owner-reentry path carries that claim on to the
        // leaf. Proxying it without the claim would 409 against the reserve's own Busy.
        if (input.ChainReserved)
            return null;

        if (!IsForwardable(workflow, transitionKey, snapshot.CurrentState))
            return null;

        // The parent decides the mode (its transition/flow executionType, else the caller's flag) and
        // sends it as the sync flag with SuppressResponseEnrichment, so the child runs in exactly it.
        var (_, mode) = TransitionRequestMode.Resolve(workflow, transitionKey, input);

        using var activity = PipelineStepActivityHelper.StartTransitionActivity("Transition.Proxy", transitionKey);
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, snapshot.Id.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, child.InstanceId.ToString());

        logger.SubFlowForwardStarted(transitionKey, child.InstanceId, snapshot.Id);

        var forwardInput = CreateForwardInput(snapshot.Id, child, input, mode);

        // ASYNC: stamp the parent's EffectiveStatus Busy BEFORE the child accepts, the way the old
        // accept-time chain reserve did. Stamping after the forward could land behind the child's
        // own rest-point relay (its job may already have run) and overwrite a newer Active. Without
        // the stamp the parent's state-function fingerprint (EffectiveStatus is a member) would stay
        // bit-identical across the accept and a long-polling client would be answered 304.
        // SYNC: no stamp — the caller blocks until the child rests and the child's relay owns the
        // projection, exactly as with the old sync forward, which never reserved the chain.
        var stamped = mode == ExecMode.Async
                      && await TryWriteEffectiveStatusAsync(
                          snapshot.Id, expected: null, InstanceStatus.Busy, transitionKey, child.InstanceId, cancellationToken);

        // The child's hops anchor under this proxy, and the lane left behind is remembered so the
        // eventual resume returns to the parent's level — the same lane handoff the post-commit
        // ForwardToSubflowJobHandler performs.
        Result<TransitionOutput> result;
        try
        {
            using (WorkflowTraceLane.EnterChildLane())
            {
                result = await subflowForwardingService.ForwardTransitionAsync(
                    child.InstanceId,
                    transitionKey,
                    forwardInput,
                    cancellationToken,
                    snapshot.Id);
            }
        }
        catch
        {
            if (stamped)
                await RevertStampAsync(snapshot, transitionKey, child.InstanceId, CancellationToken.None);
            throw;
        }

        if (!result.IsSuccess)
        {
            logger.SubFlowForwardFailed(
                child.InstanceId,
                snapshot.Id,
                transitionKey,
                result.Error.Code,
                result.Error.Message ?? string.Empty);

            // The child's own 409 Busy means it really is busy — the Busy projection is true, keep it.
            // Anything else accepted nothing, so the pre-stamp is undone (only while it is still ours).
            if (stamped && result.Error.Code != WorkflowErrorCodes.InstanceBusy)
                await RevertStampAsync(snapshot, transitionKey, child.InstanceId, cancellationToken);

            // The completion window: the child already finished (InstanceCompleted), or its row is
            // not there behind a still-open correlation (InstanceNotFound). Nothing was accepted and
            // the next attempt finds the parent settled — a 409 the client retries, naming the
            // instance the client called, not the child's error. Every level maps it again, so the
            // root answers with its own id.
            if (result.Error.Code is WorkflowErrorCodes.InstanceCompleted or WorkflowErrorCodes.InstanceNotFound)
                return Result<TransitionOutput>.Fail(WorkflowErrors.InstanceBusy(snapshot.Id, transitionKey));

            return Result<TransitionOutput>.Fail(result.Error);
        }

        var childStatus = result.Value!.Status;

        // A child that completed inside a sync forward resumed (or faulted) the parent in another
        // scope; only a fresh read says where that left the parent.
        var status = childStatus?.Equals(InstanceStatus.Completed) == true
            ? await ReadParentStatusAsync(snapshot, cancellationToken)
            : childStatus;

        logger.SubFlowForwardSucceeded(transitionKey, child.InstanceId, snapshot.Id);

        return Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = snapshot.Id,
            Status = status,
            ExecutedAsync = mode == ExecMode.Async
        });
    }

    /// <summary>
    /// Undoes the async pre-stamp after a forward that accepted nothing: back to the projection the
    /// snapshot read, and only while the column still holds the Busy this proxy wrote — a value the
    /// child's relay has written since is newer and stays.
    /// </summary>
    private async Task RevertStampAsync(
        InstanceExecutionSnapshot snapshot,
        string transitionKey,
        Guid childId,
        CancellationToken cancellationToken)
    {
        if (snapshot.EffectiveStatus is not { } prior || prior.Equals(InstanceStatus.Busy))
            return;

        await TryWriteEffectiveStatusAsync(
            snapshot.Id, expected: InstanceStatus.Busy, prior, transitionKey, childId, cancellationToken);
    }

    /// <summary>
    /// One committed EffectiveStatus write in its own RequiresNew UoW: unguarded when
    /// <paramref name="expected"/> is null, compare-and-set otherwise. Never throws — the projection
    /// is a cache-validation aid, not ownership, and a failure here must neither block the forward
    /// nor fail a request the child already accepted.
    /// </summary>
    /// <returns>True when the write was committed (for a CAS: and it matched).</returns>
    private async Task<bool> TryWriteEffectiveStatusAsync(
        Guid parentId,
        InstanceStatus? expected,
        InstanceStatus effectiveStatus,
        string transitionKey,
        Guid childId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var uow = unitOfWorkManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
            var written = true;
            if (expected is null)
                await instanceRepository.SetEffectiveStatusAsync(parentId, effectiveStatus, cancellationToken);
            else
                written = await instanceRepository.TryCompareAndSetEffectiveStatusAsync(
                    parentId, expected, effectiveStatus, cancellationToken);
            await uow.CommitAsync(cancellationToken);
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.SubFlowProxyEffectiveStatusWriteFailed(ex, transitionKey, childId, parentId, effectiveStatus.Code);
            return false;
        }
    }

    /// <summary>
    /// Forwardable = admission kind Normal (not updateData / cancel / exit), not the workflow timeout,
    /// and not a parent shared transition available in the parent's current state — that one runs on
    /// the parent (<see cref="SubFlowBypassSpecification"/>).
    /// </summary>
    private bool IsForwardable(Definitions.Workflow workflow, string transitionKey, string? currentState)
    {
        if (transitionAdmissionService.ClassifyKey(workflow, transitionKey) != AdmissionKind.Normal)
            return false;

        if (string.Equals(transitionKey, WellKnownTransitionKeys.Timeout, StringComparison.OrdinalIgnoreCase)
            || (workflow.Timeout?.Key is { } timeoutKey
                && string.Equals(transitionKey, timeoutKey, StringComparison.OrdinalIgnoreCase)))
            return false;

        return !SubFlowBypassSpecification.IsParentSharedTransitionAndAvailable(workflow, transitionKey, currentState);
    }

    /// <summary>
    /// The child's input: the CHILD's coordinates, the client's body and context, the parent id header
    /// (same block as <c>ForwardToSubflowJobHandler</c>), and the parent's resolved mode. Carries the
    /// server-only trust flag as it came in and claims no reserve (none was taken).
    /// </summary>
    private static TransitionInput CreateForwardInput(
        Guid parentId,
        ActiveSubFlowRef child,
        TransitionInput input,
        ExecMode mode)
    {
        // HTTP headers are case-insensitive. Build with OrdinalIgnoreCase and copy with last-wins
        // semantics so a parent-instance-id already present (possibly lower-cased by an upstream hop)
        // collapses into a single entry; stamping the current parent id then REPLACES it.
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in input.Headers)
            headers[kv.Key] = kv.Value;
        headers[TelemetryConstants.HeaderNames.ParentInstanceId] = parentId.ToString();

        return new TransitionInput(
            child.Domain,
            child.Flow,
            input.Data is null
                ? null
                : new TransitionDataInput(input.Data.Attributes)
                {
                    Key = input.Data.Key,
                    Tags = input.Data.Tags,
                    Stage = input.Data.Stage
                },
            sync: mode == ExecMode.Sync)
        {
            Headers = headers,
            RouteValues = input.RouteValues,
            Termination = input.Termination,
            Actor = input.Actor,
            CorrelationId = input.CorrelationId,
            ChainReserved = false,
            // Server-only, never bound from a request: a DirectTrigger body that lands on a parent
            // stays trusted at the leaf, which is where it is recorded.
            TrustedPayload = input.TrustedPayload,
            // Keeps the child from re-resolving its own executionType, and the response identity-only:
            // the client's body is shaped from the parent.
            SuppressResponseEnrichment = true
        };
    }

    private async Task<InstanceStatus?> ReadParentStatusAsync(
        InstanceExecutionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var fresh = await instanceRepository.FindByIdentifierSlimAsync(snapshot.Id.ToString(), cancellationToken);
        return fresh?.Status ?? snapshot.Status;
    }
}
