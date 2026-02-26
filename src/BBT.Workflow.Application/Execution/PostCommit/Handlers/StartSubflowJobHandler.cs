using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.PostCommit.Handlers;

/// <summary>
/// Post-commit handler for starting subflow instances.
/// Executes after the distributed lock is released to avoid deadlocks.
/// </summary>
public sealed class StartSubflowJobHandler(
    IInstanceRepository instanceRepository,
    ISubflowStarter subflowStarter,
    IScriptContextFactory scriptContextFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<StartSubflowJobHandler> logger) : IPostCommitHandler<StartSubflowJob>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        StartSubflowJob job,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.InstanceId] = context.InstanceId
        }))
        {
            // Refresh instance to get the correlation that was added during the step
            var instanceResult = await instanceRepository.GetResultAsync(context.InstanceId.ToString(), true, cancellationToken);
            if (!instanceResult.IsSuccess)
            {
                logger.SubFlowInstanceNotFound(context.InstanceId, job.CorrelationId);
                return Result.Fail(instanceResult.Error);
            }

            var instance = instanceResult.Value!;

            // Find the correlation that was created during the step
            var correlation = instance.ChildCorrelations.SingleOrDefault(x => x.Id == job.CorrelationId);
            if (correlation is null)
            {
                logger.SubFlowCorrelationNotFoundForStart(job.CorrelationId, context.InstanceId);
                return Result.Fail(WorkflowErrors.SubFlowCorrelationNotFound(job.CorrelationId, context.InstanceId));
            }

            // Resolve target state from job's target state key
            var target = context.Workflow.States.SingleOrDefault(s => s.Key == job.TargetStateKey);
            if (target?.SubFlow is null)
            {
                logger.SubFlowTargetStateNotFound(job.TargetStateKey, context.InstanceId);
                return Result.Fail(WorkflowErrors.SubFlowTargetStateNotFound(job.TargetStateKey, context.InstanceId));
            }

            // Build script context for subflow mapping
            await using var scriptContext = await CreateScriptContextAsync(context, instance, cancellationToken);

            // Start the subflow (Result pattern - no try-catch needed)
            var startResult = await subflowStarter.StartAsync(
                context.Workflow,
                instance,
                target,
                context.Transition!,
                correlation,
                scriptContext,
                cancellationToken);

            if (startResult.IsSuccess)
            {
                logger.SubFlowStarted(job.TargetStateKey, context.InstanceId);
            }

            return startResult;
        }
    }

    /// <summary>
    /// Creates a script context for SubFlow operations.
    /// </summary>
    private async Task<ScriptContext> CreateScriptContextAsync(
        TransitionExecutionContext context,
        Instance instance,
        CancellationToken cancellationToken)
    {
        return await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(context.Workflow)
            .WithInstance(instance)
            .WithTransition(context.Transition!)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(context.Data)
            .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .BuildAsync(cancellationToken);
    }
}

