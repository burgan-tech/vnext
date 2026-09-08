using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that executes the target state's OnEntry tasks.
/// These tasks run when entering the new state.
/// Uses Result pattern for exception-free error handling.
/// Integrates with Error Boundary for task-level error handling.
/// Supports task bypass during retry to avoid duplicate execution.
/// </summary>
public sealed class RunOnEntryTasksStep(
    ITaskCoordinatorExtended taskCoordinator,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IInstanceTaskRepository instanceTaskRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<RunOnEntryTasksStep> logger) : ITransitionStep
{
    /// <summary>
    /// Context key for storing failed OnExecuteTask for Error Boundary.
    /// </summary>
    public const string FailedOnExecuteTaskKey = "FailedOnExecuteTask";

    /// <summary>
    /// Context key for storing TaskExecutionError for Error Boundary.
    /// </summary>
    public const string TaskExecutionErrorKey = "TaskExecutionError";

    /// <inheritdoc />
    public int Order => LifecycleOrder.OnEntry;
    
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        // Skip if no OnEntry tasks
        if (!HasOnEntryTasks(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Railway chain: Build context -> Get successful tasks -> Execute remaining -> Apply changes -> Persist
        var scriptContext = await BuildScriptContextAsync(context, cancellationToken);
        
        // Get task IDs that completed with business success for bypass during retry
        // Only bypass tasks that succeeded at business level (not just platform level)
        var successfulTaskIds = await GetSuccessfulTaskIdsAsync(context, cancellationToken);
        
        var executeResult = await ExecuteTasksWithDetailsAsync(context, scriptContext, successfulTaskIds, cancellationToken);
        if (!executeResult.IsSuccess)
        {
            return Result<StepOutcome>.Fail(executeResult.Error);
        }

        var tasksResult = executeResult.Value!;

        // Check for boundary action - handled errors
        if (tasksResult.BoundaryAction != null)
        {
            // Store error context for logging/debugging
            if (tasksResult.FailedTask != null)
                context.Items[FailedOnExecuteTaskKey] = tasksResult.FailedTask;
            if (tasksResult.TaskError != null)
                context.Items[TaskExecutionErrorKey] = tasksResult.TaskError;

            // Task outputs are already persisted per record by the write service; only the
            // aggregate's non-data changes (mutations, sync of the latest snapshot) remain.
            context.ApplyScriptContextChanges(scriptContext);

            // Record the incident BEFORE the save, so the save commits it together with the
            // HasActiveIncident flag. Order matters: an abort returns Fail, and the pipeline's fault
            // path then reloads the instance in its OWN unit of work and adds a fallback incident
            // unless the committed flag already says one exists. Recording after the save left the
            // flag false at that moment and produced two incidents for one failure — the boundary's
            // verdict and a bare pipeline row, with the pipeline row the newer of the two.
            var outcome = BoundaryOutcomeHandler.Handle(context, tasksResult, logger);
            await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

            return outcome;
        }

        // Unhandled failure - this will cause fault
        if (tasksResult is { IsSuccess: false, TaskError: not null })
        {
            context.Items[FailedOnExecuteTaskKey] = tasksResult.FailedTask;
            context.Items[TaskExecutionErrorKey] = tasksResult.TaskError;

            BoundaryOutcomeHandler.RecordUnhandledIncident(context, tasksResult.TaskError, logger);

            // Same reason as the boundary path above: the incident has to be committed here or the
            // fault path records a duplicate. ApplyScriptContextChanges is deliberately NOT called —
            // its only payload is a script's Stage mutation, and persisting that from a run which is
            // about to fault would be an unrelated behaviour change.
            //
            // Best-effort: if the save fails, the transition still fails with its ORIGINAL task
            // error rather than a DbUpdate error, and the fault path's fallback incident keeps the
            // failure visible.
            try
            {
                await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.IncidentPersistFailed(exception, context.Instance.Id, context.TransitionKey);
            }

            return Result<StepOutcome>.Fail(tasksResult.TaskError.ToError());
        }

        // Non-blocking business failures (no ErrorBoundary) are expected to be routed by AutoTransitions.
        // If epilogue is skipped, they become unhandled and must fault the transition.
        if (tasksResult is { IsSuccess: true, HasFailedTasks: true })
        {
            NonBlockingTaskFailures.Add(context, trigger: "OnEntry", tasksResult);

            if (context.Target?.AutoTransitions == null || !context.Target.AutoTransitions.Any())
            {
                return Result<StepOutcome>.Fail(
                    ExecutionErrors.UnhandledNonBlockingTaskFailures(
                        context.TransitionKey,
                        context.Target?.Key ?? context.Current.Key,
                        NonBlockingTaskFailures.Get(context),
                        reason: "NoAutoTransitions"));
            }

            if (context.Directives.Epilogue == EpilogueMode.Skip)
            {
                return Result<StepOutcome>.Fail(
                    ExecutionErrors.UnhandledNonBlockingTaskFailures(
                        context.TransitionKey,
                        context.Target?.Key ?? context.Current.Key,
                        NonBlockingTaskFailures.Get(context),
                        reason: "EpilogueSkipped"));
            }
        }
        
        context.ApplyScriptContextChanges(scriptContext);
        await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
        
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    /// <summary>
    /// Checks if context has OnEntry tasks.
    /// </summary>
    private static bool HasOnEntryTasks(TransitionExecutionContext context)
        => context.Target?.OnEntries != null && context.Target.OnEntries.Any();

    /// <summary>
    /// Builds or retrieves script context.
    /// </summary>
    private async Task<ScriptContext> BuildScriptContextAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        return await context.GetOrBuildScriptContextAsync(
            ct => CreateScriptContextAsync(context, ct),
            cancellationToken);
    }

    /// <summary>
    /// Gets the IDs of tasks that have completed with business success for this transition.
    /// These tasks will be bypassed during retry to avoid duplicate execution.
    /// Only tasks with BusinessStatus.Success are bypassed; failed tasks will be retried.
    /// </summary>
    private async Task<IEnumerable<string>> GetSuccessfulTaskIdsAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // A transition record inserted by this pipeline run cannot have task journal rows yet.
        // Keep the lookup only for retries, where the original transition record is reused.
        if (IsFreshTransitionRecord(context))
        {
            return [];
        }

        var transitionId = GetTransitionRecordId(context);
        if (!transitionId.HasValue)
        {
            return [];
        }

        return await instanceTaskRepository.GetSuccessfulTaskIdsAsync(transitionId.Value, cancellationToken);
    }

    /// <summary>
    /// Executes the OnEntry tasks with detailed results for Error Boundary.
    /// Bypasses tasks that completed with business success.
    /// </summary>
    private async Task<Result<TasksExecutionResult>> ExecuteTasksWithDetailsAsync(
        TransitionExecutionContext context,
        ScriptContext scriptContext,
        IEnumerable<string> successfulTaskIds,
        CancellationToken cancellationToken)
    {
        var instanceTransitionId = GetTransitionRecordId(context);

        return await taskCoordinator.ExecuteWithDetailsAsync(
            context.Target!.OnEntries,
            instanceTransitionId,
            TaskTrigger.OnEntry,
            TaskExecutionOrigin.Flow,
            scriptContext,
            successfulTaskIds,
            // A freshly inserted transition record cannot have journal rows, so the engine skips
            // its per-task idempotency probe; a retry (reused record) keeps it.
            skipJournalProbe: IsFreshTransitionRecord(context),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets transition record ID from context items.
    /// </summary>
    private static Guid? GetTransitionRecordId(TransitionExecutionContext context)
        => context.Items.TryGetValue("TransitionRecordId", out var record) ? record as Guid? : null;

    /// <summary>
    /// Creates a script context for task execution.
    /// </summary>
    private async Task<ScriptContext> CreateScriptContextAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var instanceTransition = context.Items.TryGetValue("InstanceTransition", out var it) && it is InstanceTransition inst
            ? inst
            : null;

        var builder = scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(context.Workflow)
            .WithInstance(context.Instance)
            .WithBody(context.Data)
            .WithRuntime(runtimeInfoProvider)
            .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .WithCurrentTransition(instanceTransition);

        if (context.Transition != null)
            builder = builder.WithTransition(context.Transition);

        return await builder.BuildAsync(cancellationToken);
    }

    /// <summary>
    /// True when CreateTransitionRecordStep INSERTED the record in this run (see
    /// <see cref="CreateTransitionRecordStep.TransitionRecordFreshKey"/>); false on retries.
    /// </summary>
    private static bool IsFreshTransitionRecord(TransitionExecutionContext context)
        => context.Items.TryGetValue(CreateTransitionRecordStep.TransitionRecordFreshKey, out var fresh)
           && fresh is true;
}
