using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <summary>
/// Service for handling instance cancellation operations.
/// Processes job cleanup when an instance is canceled.
/// </summary>
/// <remarks>
/// This service encapsulates the business logic for instance cancellation,
/// making it reusable across different consumers (handlers, hooks, controllers).
/// </remarks>
public sealed class InstanceCancellationService(
    IInstanceRepository instanceRepository,
    IInstanceJobRepository instanceJobRepository,
    IBackgroundJobService backgroundJobService,
    IResourceLockService resourceLockService,
    IUnitOfWorkManager uowManager,
    ILogger<InstanceCancellationService> logger)
    :  IInstanceCancellationService
{
    /// <inheritdoc />
    public async Task<Result> ProcessCancellationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.InstanceId] = instanceId
        }))
        try
        {
            var instance = await instanceRepository.FindAsync(instanceId, true, cancellationToken);

            if (instance == null)
            {
                logger.InstanceNotFound(instanceId, string.Empty);
                return Result.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString()));
            }

            // Release any distributed resource locks this instance holds. Runs on every terminal
            // path (completed / faulted / canceled) and before the job-cleanup early return, so a
            // lock is freed the moment the instance reaches a terminal state — no per-transition
            // Release wiring, and no leak until TTL on unexpected faults. Best-effort and idempotent.
            await ReleaseTrackedResourceLocksAsync(instance, cancellationToken);

            var jobs = await instanceJobRepository.GetListActiveAsync(instance.Id, cancellationToken);

            if (!jobs.Any())
            {
                return Result.Ok();
            }

            var processedJobCount = 0;
            foreach (var job in jobs)
            {
                try
                {
                    if (await ProcessJobCancellationAsync(job, instance.Id, cancellationToken))
                    {
                        processedJobCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId);
                }
            }

            logger.InstanceCanceledJobsProcessed(instanceId, processedJobCount);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            logger.InstanceCanceledProcessingFailed(ex, instanceId);
            return Result.Fail(WorkflowErrors.InstanceCancellationFailed(instanceId, ex.Message));
        }
    }
    
    /// <inheritdoc />
    public async Task<Result> ProcessStateTransitionsCancellationAsync(
        Guid instanceId,
        string? sourceState,
        IReadOnlyList<string> transitionKeys,
        CancellationToken cancellationToken = default)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.InstanceId] = instanceId
        }))
        try
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            var instance = await instanceRepository.FindAsync(instanceId, true, cancellationToken);
            if (instance == null)
            {
                logger.InstanceNotFound(instanceId, string.Empty);
                return Result.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString()));
            }

            // The caller (e.g. CancelScheduledJobsStep) already resolved which transitions must be
            // cancelled, so we simply match this instance's active jobs by their targeted key —
            // no extra job-type conditioning. Matching uses the structured TransitionKey column
            // instead of the previous fragile JobName suffix parse.
            var allJobs = await instanceJobRepository.GetListActiveAsync(instance.Id, cancellationToken);

            var jobsToCancel = allJobs
                .Where(job =>
                    // Source-state-scoped match: only cancel jobs owned by the state being left, so a
                    // same-named transition on another state's timer is not cancelled by mistake.
                    (job.SourceState == sourceState
                        && job.TransitionKey != null
                        && transitionKeys.Contains(job.TransitionKey))
                    // Transitional fallback for pre-rollout rows (no structured columns):
                    // old "-{key}" suffix match. Removable once no legacy rows remain.
                    || (job.JobType == JobType.Unknown
                        && transitionKeys.Any(key => job.JobName.EndsWith($"-{key}", StringComparison.Ordinal))))
                .ToList();

            if (!jobsToCancel.Any())
            {
                return Result.Ok();
            }

            var processedJobCount = 0;
            foreach (var job in jobsToCancel)
            {
                try
                {
                    if (await ProcessJobCancellationAsync(job, instance.Id, cancellationToken))
                    {
                        processedJobCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId);
                }
            }

            logger.StateTransitionsJobsCanceled(
                processedJobCount,
                instanceId,
                string.Join(", ", transitionKeys));

            await uow.CommitAsync(cancellationToken);

            return Result.Ok();
        }
        catch (Exception ex)
        {
            logger.InstanceCanceledProcessingFailed(ex, instanceId);
            return Result.Fail(WorkflowErrors.InstanceCancellationFailed(instanceId, ex.Message));
        }
    }

    /// <summary>
    /// Releases every distributed resource lock recorded on the instance (owner = instance ID).
    /// Best-effort: release is idempotent (an already-expired/released lock is a no-op) and a failure
    /// to release one key must never abort terminal cleanup — the lock's TTL is the ultimate safety net.
    /// The tracked key set is intentionally left in metadata (re-release is harmless) to avoid an
    /// extra persistence write on the cleanup path.
    /// </summary>
    private async Task ReleaseTrackedResourceLocksAsync(Instance instance, CancellationToken cancellationToken)
    {
        var lockKeys = instance.GetTrackedResourceLocks();
        if (lockKeys.Count == 0)
        {
            return;
        }

        var owner = instance.Id.ToString();
        foreach (var lockKey in lockKeys)
        {
            try
            {
                await resourceLockService.ReleaseAsync(lockKey, owner, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.ResourceLockAutoReleaseError(ex, lockKey, instance.Id);
            }
        }
    }

    private async Task<bool> ProcessJobCancellationAsync(
        InstanceJob job,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var outcome = await backgroundJobService.CancelWaitingAsync(job.JobId, cancellationToken);
        if (outcome == BackgroundJobCancellationResult.SkippedRunning)
        {
            logger.InstanceJobCleanupSkippedRunning(job.JobId, instanceId);
            return false;
        }

        job.MarkAsProcessed();
        await instanceJobRepository.UpdateAsync(job, false, cancellationToken);
        return true;
    }
}
