using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Instances;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Default <see cref="IStateNotificationScheduler"/> over the Aether background-job service / Dapr Jobs.
/// Mirrors <see cref="TransitionJobEnqueuer"/>: the durable <see cref="InstanceJob"/> row is written
/// inside the ambient transition unit of work (committing atomically with the transition) and the
/// Dapr schedule is deferred to post-commit via <c>directly: true</c>.
/// </summary>
public sealed class StateNotificationScheduler(
    IBackgroundJobService backgroundJobService,
    IInstanceJobRepository jobRepository,
    IOptions<WorkflowExecutionOptions> executionOptions) : IStateNotificationScheduler
{
    /// <inheritdoc />
    public async Task ScheduleAsync(
        Guid instanceId,
        string domain,
        string flowName,
        string version,
        string stateKey,
        CancellationToken cancellationToken = default)
    {
        var jobName = JobName.ForStateNotify(instanceId, stateKey);
        var activity = Activity.Current;

        var payload = new StateNotifyPayload
        {
            JobName = jobName.Value,
            Domain = domain,
            InstanceId = instanceId,
            FlowName = flowName,
            Version = version,
            StateKey = stateKey,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString
        };

        var fp = executionOptions.Value.FailurePolicy;
        var failurePolicy = JobScheduleFailurePolicy.Constant(
            TimeSpan.FromSeconds(fp.IntervalSeconds),
            (uint)fp.MaxRetries);

        var schedule = DaprJobSchedule.FromDateTime(DateTime.UtcNow.AddMilliseconds(5)).ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = domain,
            ["flowName"] = flowName,
            ["instanceId"] = instanceId.ToString()
        };

        var jobId = await backgroundJobService.EnqueueAsync(
            StateNotifyJobHandler.HandlerName,
            jobName.Value,
            payload,
            schedule,
            metadata,
            failurePolicy,
            directly: true,
            kind: JobKind.OneShot,
            cancellationToken: cancellationToken);

        await jobRepository.InsertAsync(
            InstanceJob.Create(
                jobId,
                jobName,
                jobId,
                domain,
                flowName,
                instanceId),
            true,
            cancellationToken);
    }
}
