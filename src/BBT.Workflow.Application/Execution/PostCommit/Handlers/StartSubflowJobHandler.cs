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
        var rootId = context.Instance.GetRootInstanceId();
        var scopeProps = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.InstanceId] = context.InstanceId
        };
        if (rootId != context.InstanceId)
        {
            scopeProps[TelemetryConstants.TagNames.RootInstanceId] = rootId;
        }
        using (logger.BeginScope(scopeProps))
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

            // Open the subflow's own trace lane, anchored on the enclosing
            // PostCommit.StartSubflowJob span — same reasoning as the forward handler: the child
            // instance's hops go flat underneath this span, and ParentLane records where the
            // resume belongs. Covers the sync case too, where StartAsync runs the subflow to
            // completion inline.
            using var childLane = WorkflowTraceLane.EnterChildLane();

            // Start the subflow (Result pattern - no try-catch needed)
            var startResult = await subflowStarter.StartAsync(
                context.Workflow,
                instance,
                target,
                context.Transition!,
                correlation,
                scriptContext,
                context.CallerMode,
                cancellationToken);

            if (startResult.IsSuccess)
            {
                if (scriptContext.Mutations.HasChanges)
                {
                    scriptContext.Mutations.ApplyTo(instance);
                    await instanceRepository.UpdateAsync(instance, true, cancellationToken);
                }

                // A sync subflow completes inside StartAsync and resumes/finalizes the parent
                // in its own scope, so the entity tracked in this scope still reads Busy.
                // Re-read as no-tracking so the sync response reflects the settled status.
                if (context.CallerMode == ExecMode.Sync)
                {
                    var refreshed = await instanceRepository.FindByIdentifierSlimAsync(
                        context.InstanceId.ToString(), cancellationToken);
                    if (refreshed is not null)
                    {
                        context.ClientResponse = new ClientResponse
                        {
                            Id = context.InstanceId,
                            Status = refreshed.Status
                        };
                    }
                }

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

