using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Logging;
using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that executes the transition's OnExecute tasks.
/// These tasks run before the state change occurs.
/// Uses Result pattern for exception-free error handling.
/// Integrates with Error Boundary for task-level error handling.
/// Supports task bypass during retry to avoid duplicate execution.
/// </summary>
public sealed class RunOnExecuteTasksStep(
    ITaskCoordinatorExtended taskCoordinator,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IInstanceTaskRepository instanceTaskRepository,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
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
    public int Order => LifecycleOrder.OnExecute;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(RunOnExecuteTasksStep)}");

        // Skip if no OnExecute tasks
        if (!HasOnExecuteTasks(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Build context -> Get completed tasks -> Execute remaining -> Apply changes -> Persist
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

            // Apply script context changes before handling boundary
            context.ApplyScriptContextChanges(scriptContext);
            await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

            return BoundaryOutcomeHandler.Handle(context, tasksResult);
        }

        // Unhandled failure - this will cause fault
        if (tasksResult is { IsSuccess: false, TaskError: not null })
        {
            context.Items[FailedOnExecuteTaskKey] = tasksResult.FailedTask;
            context.Items[TaskExecutionErrorKey] = tasksResult.TaskError;
            
            return Result<StepOutcome>.Fail(tasksResult.TaskError.ToError());
        }

        // Non-blocking business failures (no ErrorBoundary) are expected to be routed by AutoTransitions.
        // If epilogue is skipped, they become unhandled and must fault the transition.
        if (tasksResult is { IsSuccess: true, HasFailedTasks: true })
        {
            NonBlockingTaskFailures.Add(context, trigger: "OnExecute", tasksResult);

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
    /// Checks if context has OnExecute tasks.
    /// </summary>
    private static bool HasOnExecuteTasks(TransitionExecutionContext context)
        => context.Transition != null && context.Transition.OnExecutionTasks.Any();

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
        var transitionId = GetTransitionRecordId(context);
        if (!transitionId.HasValue)
        {
            return [];
        }

        return await instanceTaskRepository.GetSuccessfulTaskIdsAsync(transitionId.Value, cancellationToken);
    }

    /// <summary>
    /// Executes the OnExecute tasks with detailed results for Error Boundary.
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
            context.Transition!.OnExecutionTasks,
            instanceTransitionId,
            TaskTrigger.OnExecute,
            scriptContext,
            successfulTaskIds,
            cancellationToken);
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
        return await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(context.Workflow)
            .WithInstance(context.Instance)
            .WithTransition(context.Transition)
            .WithBody(context.Data)
            .WithRuntime(runtimeInfoProvider)
            .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .BuildAsync(cancellationToken);
    }
}
