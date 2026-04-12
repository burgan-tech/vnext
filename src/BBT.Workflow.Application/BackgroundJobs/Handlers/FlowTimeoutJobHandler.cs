using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles workflow timeout jobs that are triggered when workflow instances exceed their configured timeout duration.
/// This handler delegates the full state lifecycle to the TransitionPipeline, which runs OnExit hooks,
/// cancels scheduled transitions, applies the timeout state, runs OnEntry hooks, evaluates auto-transitions,
/// and handles workflow completion — mirroring how SubFlow completion resumes the pipeline.
/// </summary>
public sealed class FlowTimeoutJobHandler(
    IInstanceRepository instanceRepository,
    IInstanceJobRepository jobRepository,
    IComponentCacheStore componentCacheStore,
    IWorkflowMetrics workflowMetrics,
    IWorkflowExecutionService workflowExecutionService,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<FlowTimeoutJobHandler> logger,
    ICurrentSchema currentSchema
) : IBackgroundJobHandler<WorkflowTimeoutPayload>
{
    public const string HandlerName = "flow.timeout";

    public async Task HandleAsync(WorkflowTimeoutPayload args, CancellationToken cancellationToken)
    {
        using var activity = BackgroundJobActivityHelper.StartActivityAsChildWithLink("TimeoutJob.Execute", args);
        using (currentSchema.Use(args.FlowName))
        {
            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       [TelemetryConstants.TagNames.InstanceId] = args.InstanceId,
                       [TelemetryConstants.TagNames.Flow] = args.FlowName,
                       [TelemetryConstants.TagNames.Domain] = args.Domain,
                       [TelemetryConstants.TagNames.FlowVersion] = args.Version,
                       [TelemetryConstants.TagNames.TransitionKey] = WellKnownTransitionKeys.Timeout,
                       [TelemetryConstants.TagNames.JobName] = args.JobName
                   }))
                try
                {
                    BackgroundJobActivityHelper.EnrichActivity(activity, args);
                    BackgroundJobActivityHelper.EnrichActivityWithTransition(activity, WellKnownTransitionKeys.Timeout);

                    var workflowResult = await componentCacheStore.GetFlowAsync(args.Domain, args.FlowName,
                        args.Version, cancellationToken);

                    if (!workflowResult.IsSuccess)
                    {
                        logger.WorkflowNotFoundWarning(args.FlowName, workflowResult.Error.Code);
                        activity?.SetStatus(ActivityStatusCode.Error, "Workflow not found");
                        return;
                    }

                    var workflow = workflowResult.Value!;

                    var instance =
                        await instanceRepository.FindAsync(p => p.Id == args.InstanceId, true,
                            cancellationToken);
                    if (instance == null)
                    {
                        logger.InstanceNotFound(args.InstanceId, args.FlowName);
                        activity?.SetStatus(ActivityStatusCode.Error, "Instance not found");
                        return;
                    }

                    if (instance.IsCompleted)
                    {
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        return;
                    }

                    if (workflow.Timeout is null)
                    {
                        logger.TimeoutConfigMissing(instance.Flow);
                        activity?.SetStatus(ActivityStatusCode.Error, "Timeout config missing");
                        return;
                    }

                    // Record current status before timeout for metrics
                    var currentStatus = instance.Status.Code;

                    // Delegate the full state lifecycle to the TransitionPipeline.
                    // Resumes from SetBusy (19) so that SetBusy + CreateTransitionRecord both run
                    // before the timeout-specific steps begin. The pipeline then:
                    //   - Sets instance to Busy (SetBusy)
                    //   - Creates an InstanceTransition record with key "$timeout" (CreateTransitionRecord)
                    //   - Sets context.Target to the timeout target state (ApplyTimeoutState)
                    //   - Cancels scheduled transitions of the current state (CancelScheduledJobs)
                    //   - Runs OnExit tasks of the current state
                    //   - Applies the state change (ChangeState)
                    //   - Runs OnEntry tasks of the timeout target state
                    //   - Schedules new transitions / evaluates auto-transitions
                    //   - Calls instance.Complete() if the target state is a Finish state
                    var input = new WorkflowExecutionContext
                    {
                        Domain = workflow.Domain,
                        WorkflowKey = workflow.Key,
                        WorkflowVersion = workflow.Version,
                        InstanceId = args.InstanceId.ToString(),
                        TransitionKey = WellKnownTransitionKeys.Timeout,
                        TriggerType = TriggerType.Manual,
                        Mode = ExecMode.Resume,
                        Headers = new Dictionary<string, string?>(),
                        Actor = ExecutionActor.System,
                        RequestedAt = DateTimeOffset.UtcNow,
                        Execution = new ExecutionInfo
                        {
                            ExecutionChainId = Guid.NewGuid().ToString("N"),
                            ChainDepth = 0,
                            ResumeFrom = LifecycleOrder.SetBusy,
                            IsTimeoutTransition = true
                        }
                    };

                    var pipelineResult =
                        await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);

                    if (!pipelineResult.IsSuccess)
                    {
                        logger.LogError("Timeout pipeline failed for instance {InstanceId}: {Error}",
                            args.InstanceId, pipelineResult.Error.Message);
                        activity?.SetStatus(ActivityStatusCode.Error, pipelineResult.Error.Message);
                        return;
                    }

                    // Re-read instance to get updated Duration for metrics (set by HandleFinishStep if Finish state)
                    var updatedInstance =
                        await instanceRepository.FindAsync(p => p.Id == args.InstanceId, true, cancellationToken);
                    var durationSeconds = updatedInstance?.Duration?.TotalSeconds;

                    workflowMetrics.RecordInstanceTimedOut(instance.Flow, runtimeInfoProvider.Domain, currentStatus,
                        durationSeconds);

                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled");
                    logger.JobCancelled(args.JobName, "timeout", args.InstanceId);
                }
                catch (Exception e)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                    activity?.AddTag("error.type", e.GetType().Name);
                    logger.JobFailed(e, args.JobName, args.InstanceId);
                }
                finally
                {
                    await jobRepository.MarkAsProcessedAsync(args.InstanceId, args.JobName, CancellationToken.None);
                }
        }
    }
}