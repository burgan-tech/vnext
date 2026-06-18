using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
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
    ILogger<InstanceCancellationService> logger)
    :  IInstanceCancellationService
{
    /// <summary>
    /// Job types eligible for state-scoped cancellation: timer-based scheduled transitions and the
    /// long-poll acknowledge fallback (whose well-known key is passed as a pseudo transition key).
    /// </summary>
    private static readonly JobType[] StateCancellationJobTypes =
        [JobType.ScheduledTransition, JobType.LongPollAck];

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
                    await backgroundJobService.DeleteAsync(job.JobId, cancellationToken);
                    job.MarkAsProcessed();
                    await instanceJobRepository.UpdateAsync(job, true, cancellationToken);
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
        IReadOnlyList<string> transitionKeys,
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

            // Structured (DB-side) match: scheduled-transition and long-poll-ack jobs whose
            // targeted key is in the requested set. Async-transition (tx) jobs are intentionally
            // NOT cancelled here — they guard themselves via the instance lock. Legacy rows
            // (JobType.Unknown) are also returned for the transitional suffix-based fallback.
            var candidates = await instanceJobRepository.GetActiveForStateCancellationAsync(
                instance.Id,
                StateCancellationJobTypes,
                transitionKeys,
                cancellationToken);

            var jobsToCancel = candidates
                .Where(job => job.JobType != JobType.Unknown
                    // Transitional fallback for pre-rollout rows: old "-{key}" suffix match.
                    || transitionKeys.Any(key => job.JobName.EndsWith($"-{key}", StringComparison.Ordinal)))
                .ToList();

            if (!jobsToCancel.Any())
            {
                return Result.Ok();
            }

            foreach (var job in jobsToCancel)
            {
                try
                {
                    await backgroundJobService.DeleteAsync(job.JobId, cancellationToken);
                    job.MarkAsProcessed();
                    await instanceJobRepository.UpdateAsync(job, true, cancellationToken);
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

            return Result.Ok();
        }
        catch (Exception ex)
        {
            logger.InstanceCanceledProcessingFailed(ex, instanceId);
            return Result.Fail(WorkflowErrors.InstanceCancellationFailed(instanceId, ex.Message));
        }
    }
}

