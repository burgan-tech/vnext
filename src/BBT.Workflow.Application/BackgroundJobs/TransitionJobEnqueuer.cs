using BBT.Aether.BackgroundJob;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Default <see cref="ITransitionJobEnqueuer"/> over the Aether background-job service / Dapr Jobs.
/// </summary>
public sealed class TransitionJobEnqueuer(
    IBackgroundJobService backgroundJobService,
    IOptions<WorkflowExecutionOptions> executionOptions) : ITransitionJobEnqueuer
{
    /// <inheritdoc />
    public Task EnqueueAsync(TransitionJobPayload payload, CancellationToken cancellationToken = default)
    {
        var fp = executionOptions.Value.FailurePolicy;
        var failurePolicy = JobScheduleFailurePolicy.Constant(
            TimeSpan.FromSeconds(fp.IntervalSeconds),
            (uint)fp.MaxRetries);

        var schedule = DaprJobSchedule.FromDateTime(DateTime.UtcNow.AddMilliseconds(5)).ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = payload.Domain,
            ["flowName"] = payload.Workflow,
            ["instanceId"] = payload.InstanceId.ToString()
        };

        return backgroundJobService.EnqueueAsync(
            TransitionJobHandler.HandlerName,
            payload.JobName,
            payload,
            schedule,
            metadata,
            failurePolicy,
            cancellationToken);
    }
}
