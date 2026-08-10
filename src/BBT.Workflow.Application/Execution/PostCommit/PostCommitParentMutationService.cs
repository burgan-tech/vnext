using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Applies post-handoff settlement and failure recovery to freshly reloaded parent state.
/// </summary>
public sealed class PostCommitParentMutationService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    IInstanceStatusLock instanceStatusLock,
    IWorkflowContext workflowContext,
    IStateNotificationScheduler stateNotificationScheduler,
    ILogger<PostCommitParentMutationService> logger) : IPostCommitParentMutationService
{
    public async Task<Result<TransitionOutput>> SettleAsync(
        PostCommitParentSnapshot source,
        ContinuationSet continuations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(continuations);

        return await MutateFreshAsync(source, async (instance, ct) =>
        {
            var context = CreateFreshContext(source, instance);
            // A synchronous child callback may already have settled the same parent state and
            // scheduled its notification. The outer barrier only owns a notification when it
            // changes a freshly reloaded Busy parent to its resting Active state.
            var shouldScheduleNotification =
                continuations.ResolvedStatus == InstanceStatus.Active && instance.IsBusy;
            await TransitionSettlement.ApplyAsync(
                context,
                continuations.ResolvedStatus,
                scheduleNotification: shouldScheduleNotification,
                instanceRepository,
                stateNotificationScheduler,
                logger,
                ct);
        }, cancellationToken);
    }

    public async Task<Result<TransitionOutput>> FaultAsync(
        PostCommitParentSnapshot source,
        PostCommitFaultRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        return await MutateFreshAsync(source, async (instance, ct) =>
        {
            // A synchronous callback may already have completed or faulted the parent. Its
            // authoritative terminal result wins over a later outer post-commit failure.
            if (instance.IsCompleted)
                return;

            if (!instance.HasActiveIncident)
            {
                instance.AddIncident(InstanceIncidentFactory.Create(
                    state: instance.GetCurrentState,
                    transition: source.TransitionKey,
                    taskKey: null,
                    message: request.ErrorMessage ?? "Post-commit execution failed",
                    errorCode: request.ErrorCode,
                    errorLayer: "PostCommit",
                    stackTrace: request.StackTrace,
                    traceId: source.TraceId));
            }

            instance.Fault(source.Domain, source.CallerMode == ExecMode.Sync);
            await instanceRepository.UpdateAsync(instance, true, ct);
        }, cancellationToken);
    }

    private async Task<Result<TransitionOutput>> MutateFreshAsync(
        PostCommitParentSnapshot source,
        Func<Instance, CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        // Post-commit mutations are status flips — the short status lock serializes them,
        // and the mutation commits inside this scope before release.
        await using var lockScope = await instanceStatusLock.AcquireAsync(source.LockKey, cancellationToken);
        if (!lockScope.IsAcquired)
            return Result<TransitionOutput>.Fail(WorkflowErrors.InstanceLockConflict(source.InstanceId));

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // The authoritative output carries PipelineInstance into sync response enrichment, so
        // reload correlations for settlement decisions and data for the final response projection.
        var instance = await instanceRepository.FindWithAllCorrelationsAndDataAsync(
            source.InstanceId,
            cancellationToken);
        if (instance is null)
            return Result<TransitionOutput>.Fail(WorkflowErrors.InstanceNotFound(source.InstanceId.ToString()));

        await mutation(instance, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        return Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = instance.Id,
            Key = instance.Key,
            Status = instance.Status,
            PipelineInstance = instance
        });
    }

    private TransitionExecutionContext CreateFreshContext(
        PostCommitParentSnapshot source,
        Instance instance)
    {
        var workflow = workflowContext.Workflow
                       ?? throw new InvalidOperationException(
                           "A workflow scope is required for post-commit parent settlement.");
        var freshState = workflow.FindState(instance.GetCurrentState);

        return new TransitionExecutionContext
        {
            Domain = source.Domain,
            WorkflowKey = source.WorkflowKey,
            InstanceId = source.InstanceId,
            TransitionKey = source.TransitionKey,
            CallerMode = source.CallerMode,
            Mode = source.CallerMode,
            TraceId = source.TraceId,
            Headers = source.Headers,
            RouteValues = source.RouteValues,
            Data = source.Data,
            Workflow = workflow,
            Instance = instance,
            Current = freshState!,
            Target = freshState
        };
    }
}
