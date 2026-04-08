using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Executor interface for processing post-commit jobs.
/// Handles job dispatch and execution after the distributed lock is released.
/// </summary>
public interface IPostCommitExecutor
{
    /// <summary>
    /// Executes the given post-commit jobs.
    /// Each job is dispatched to its corresponding handler.
    /// </summary>
    /// <param name="jobs">The collection of jobs to execute.</param>
    /// <param name="context">The transition execution context.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A PostCommitResult indicating success, failure, and fault request.</returns>
    Task<PostCommitResult> ExecuteAsync(
        IReadOnlyList<IPostCommitJob> jobs,
        TransitionExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default implementation of IPostCommitExecutor.
/// Creates a new service scope for handler resolution and executes jobs sequentially.
/// Supports idempotency checking and failure policy for decision making.
/// </summary>
public sealed class PostCommitExecutor(
    IServiceScopeFactory scopeFactory,
    IPostCommitFailurePolicy failurePolicy,
    IPostCommitIdempotencyStore idempotencyStore,
    ILogger<PostCommitExecutor> logger) : IPostCommitExecutor
{
    /// <inheritdoc />
    public async Task<PostCommitResult> ExecuteAsync(
        IReadOnlyList<IPostCommitJob> jobs,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
            return PostCommitResult.Ok();

        logger.PostCommitExecutorStarting(context.InstanceId, jobs.Count);

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   [TelemetryConstants.TagNames.Domain]          = context.Domain,
                   [TelemetryConstants.TagNames.Flow]          = context.Workflow.Key,
                   [TelemetryConstants.TagNames.FlowVersion]          = context.Workflow.Version,
                   [TelemetryConstants.TagNames.InstanceId]    = context.InstanceId,
                   [TelemetryConstants.TagNames.InstanceKey]    = context.Instance.Key ?? "N/A",
                   [TelemetryConstants.TagNames.StateFrom]     = context.Transition?.From ?? context.Instance.GetCurrentState,
                   [TelemetryConstants.TagNames.StateTo]       = context.Transition?.Target ?? "N/A",
                   [TelemetryConstants.TagNames.TransitionKey] = context.TransitionKey,
                   [TelemetryConstants.TagNames.TriggerType] = context.Transition?.TriggerType.ToString() ?? "N/A"
               }))
        {
            // Post-commit jobs run outside the lock but in the same request scope.
            // Use a new scope for isolation and proper DI lifetime management.
            // Update AmbientServiceProvider.Current so that domain events raised during
            // post-commit handlers resolve hook invokers from the correct scope.
            var previousAmbient = AmbientServiceProvider.Current;
            await using var scope = scopeFactory.CreateAsyncScope();
            var serviceProvider = scope.ServiceProvider;
            AmbientServiceProvider.Current = serviceProvider;
            try
            {
                List<Error>? errors = null;

                for (var i = 0; i < jobs.Count; i++)
                {
                    var job = jobs[i];

                    // 1) Idempotency check for idempotent jobs
                    if (job is IIdempotentPostCommitJob idempotentJob)
                    {
                        var beginResult =
                            await idempotencyStore.TryBeginAsync(idempotentJob.IdempotencyKey, cancellationToken);
                        if (!beginResult.IsSuccess)
                        {
                            // Store operation failed - treat as job failure
                            logger.LogWarning(
                                "Idempotency store failed for job {JobType} with key {Key}: {Error}",
                                job.GetType().Name,
                                idempotentJob.IdempotencyKey,
                                beginResult.Error.Message);

                            return CreateFailureResult(job, beginResult.Error, i, jobs.Count);
                        }

                        if (!beginResult.Value)
                        {
                            // Job already processed - skip safely
                            logger.LogDebug(
                                "Skipping duplicate job {JobType} with idempotency key {Key}",
                                job.GetType().Name,
                                idempotentJob.IdempotencyKey);
                            continue;
                        }
                    }

                    // 2) Execute handler
                    var execResult = await DispatchAsync(job, serviceProvider, context, cancellationToken);

                    if (execResult.IsSuccess)
                    {
                        // Mark idempotent job as completed
                        if (job is IIdempotentPostCommitJob completedJob)
                        {
                            await idempotencyStore.MarkCompletedAsync(completedJob.IdempotencyKey, cancellationToken);
                        }

                        logger.PostCommitJobCompleted(context.InstanceId, job.GetType().Name);
                        continue;
                    }

                    // Handler failed
                    logger.PostCommitJobFailed(context.InstanceId, job.GetType().Name,
                        execResult.Error.Message ?? "Unknown error");

                    // Mark idempotent job as failed
                    if (job is IIdempotentPostCommitJob failedJob)
                    {
                        await idempotencyStore.MarkFailedAsync(
                            failedJob.IdempotencyKey,
                            execResult.Error.Code,
                            execResult.Error.Message,
                            cancellationToken);
                    }

                    // 3) Consult failure policy
                    var decision =
                        failurePolicy.Decide(new PostCommitFailureContext(job, execResult.Error, i, jobs.Count));

                    errors ??= new List<Error>(capacity: 4);
                    errors.Add(execResult.Error);

                    if (decision.ShouldMarkInstanceFaulted)
                    {
                        return PostCommitResult.Fail(
                            execResult.Error,
                            new PostCommitFaultRequest(decision.FaultErrorCode, decision.FaultErrorMessage));
                    }

                    if (!decision.ShouldContinue)
                    {
                        return PostCommitResult.Fail(execResult.Error);
                    }
                }

                // Continue mode: all jobs processed but some may have failed
                if (errors is { Count: > 0 })
                {
                    // Return first error (could aggregate if needed)
                    return PostCommitResult.Fail(errors[0]);
                }

                logger.PostCommitExecutorCompleted(context.InstanceId, jobs.Count);
                return PostCommitResult.Ok();
            }
            finally
            {
                AmbientServiceProvider.Current = previousAmbient;
            }
        }
    }

    /// <summary>
    /// Creates a failure result with fault request based on policy.
    /// Used when idempotency store fails.
    /// </summary>
    private PostCommitResult CreateFailureResult(IPostCommitJob job, Error error, int index, int total)
    {
        var decision = failurePolicy.Decide(new PostCommitFailureContext(job, error, index, total));

        if (decision.ShouldMarkInstanceFaulted)
        {
            return PostCommitResult.Fail(
                error,
                new PostCommitFaultRequest(decision.FaultErrorCode, decision.FaultErrorMessage));
        }

        return PostCommitResult.Fail(error);
    }

    /// <summary>
    /// Dispatches a job to its corresponding handler.
    /// Dynamically resolves IPostCommitHandler&lt;TJob&gt; from DI using reflection.
    /// </summary>
    private static async Task<Result> DispatchAsync(
        IPostCommitJob job,
        IServiceProvider serviceProvider,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var jobType = job.GetType();
        var handlerInterfaceType = typeof(IPostCommitHandler<>).MakeGenericType(jobType);

        var handler = serviceProvider.GetService(handlerInterfaceType);
        if (handler is null)
        {
            return Result.Fail(WorkflowErrors.ConfigInvalid(
                context.InstanceId,
                $"No post-commit handler registered for: {jobType.Name}"));
        }

        // Invoke HandleAsync via reflection
        var handleMethod = handlerInterfaceType.GetMethod(nameof(IPostCommitHandler<IPostCommitJob>.HandleAsync))!;
        var resultTask = (Task<Result>)handleMethod.Invoke(handler, [job, context, cancellationToken])!;

        return await resultTask;
    }
}