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
    /// <inheritdoc />
    public async Task<Result> ProcessCancellationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
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

            // Process all jobs in parallel for better performance
            var processingTasks = jobs.Select(async job =>
            {
                await backgroundJobService.DeleteAsync(job.JobId, cancellationToken);
                job.MarkAsProcessed();
                await instanceJobRepository.UpdateAsync(job, true, cancellationToken);
            });

            await Task.WhenAll(processingTasks);

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
        try
        {
            var instance = await instanceRepository.FindAsync(instanceId, true, cancellationToken);
            if (instance == null)
            {
                logger.InstanceNotFound(instanceId, string.Empty);
                return Result.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString()));
            }

            // Get all active jobs for this instance
            var allJobs = await instanceJobRepository.GetListActiveAsync(instance.Id, cancellationToken);
            
            // Filter jobs by transition keys
            // Job name format: trans-{instanceId}-{transitionKey}
            var jobsToCancel = allJobs.Where(job => 
                transitionKeys.Any(key => job.JobName.EndsWith($"-{key}"))).ToList();
            
            if (!jobsToCancel.Any())
            {
                return Result.Ok();
            }

            // Process filtered jobs in parallel
            var processingTasks = jobsToCancel.Select(async job =>
            {
                await backgroundJobService.DeleteAsync(job.JobId, cancellationToken);
                job.MarkAsProcessed();
                await instanceJobRepository.UpdateAsync(job, true, cancellationToken);
            });

            await Task.WhenAll(processingTasks);

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

