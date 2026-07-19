using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Aether.Uow;
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

            var jobs = await instanceJobRepository.GetListActiveAsync(instance.Id, cancellationToken);

            if (!jobs.Any())
            {
                return Result.Ok();
            }

            foreach (var job in jobs)
            {
                try
                {
                    await ProcessJobCancellationAsync(job, instance.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId);
                }
            }

            logger.InstanceCanceledJobsProcessed(instanceId, jobs.Count);

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

            foreach (var job in jobsToCancel)
            {
                try
                {
                    await ProcessJobCancellationAsync(job, instance.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId);
                }
            }

            logger.StateTransitionsJobsCanceled(
                jobsToCancel.Count,
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

    private async Task ProcessJobCancellationAsync(
        InstanceJob job,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var outcome = await backgroundJobService.CancelWaitingAsync(job.JobId, cancellationToken);
        if (outcome == BackgroundJobCancellationResult.SkippedRunning)
        {
            logger.InstanceJobCleanupSkippedRunning(job.JobId, instanceId);
            return;
        }

        job.MarkAsProcessed();
        await instanceJobRepository.UpdateAsync(job, false, cancellationToken);
    }
}
