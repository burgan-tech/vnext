using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Default <see cref="IStateNotificationScheduler"/> over the Aether background-job service / Dapr Jobs.
/// Mirrors <see cref="TransitionJobEnqueuer"/>: the durable <see cref="InstanceJob"/> row is written
/// inside the ambient transition unit of work (committing atomically with the transition) and the
/// Dapr schedule is deferred to post-commit via <c>directly: true</c>. The settled state's request
/// context (headers, route values, body) is captured into the payload so rule/mapping scripts can run
/// against a full <c>ScriptContext</c> in the durable job.
/// </summary>
public sealed class StateNotificationScheduler(
    IBackgroundJobService backgroundJobService,
    IInstanceJobRepository jobRepository,
    IOptions<WorkflowExecutionOptions> executionOptions) : IStateNotificationScheduler
{
    /// <inheritdoc />
    public async Task ScheduleAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var instanceId = context.InstanceId;
        var stateKey = context.Target!.Key;
        var jobName = JobName.ForStateNotify(instanceId, stateKey);
        var activity = Activity.Current;

        var payload = new StateNotifyPayload
        {
            JobName = jobName.Value,
            Domain = context.Domain,
            InstanceId = instanceId,
            FlowName = context.WorkflowKey,
            Version = context.Workflow.Version,
            StateKey = stateKey,
            Headers = context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            RouteValues = context.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Data = context.Data is null
                ? null
                : JsonSerializer.SerializeToElement(context.Data, JsonSerializerConstants.JsonOptions),
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString
        };

        var fp = executionOptions.Value.FailurePolicy;
        var failurePolicy = JobScheduleFailurePolicy.Constant(
            TimeSpan.FromSeconds(fp.IntervalSeconds),
            (uint)fp.MaxRetries);

        // Immediate, same as the transition enqueuer: no artificial lead. See TransitionJobEnqueuer
        // for why the former 5 ms was not protecting anything, and why this must stay an ISO-8601
        // instant rather than a zero duration.
        var schedule = DaprJobSchedule.FromDateTime(DateTime.UtcNow).ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = context.Domain,
            ["flowName"] = context.WorkflowKey,
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
                context.Domain,
                context.WorkflowKey,
                instanceId),
            true,
            cancellationToken);
    }
}
