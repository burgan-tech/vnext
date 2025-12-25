using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Logging;
using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that executes the transition's OnExecute tasks.
/// These tasks run before the state change occurs.
/// Uses Result pattern for exception-free error handling.
/// Error boundary handling is applied transparently via TaskExecutorInvoker.
/// </summary>
public sealed class RunOnExecuteTasksStep(
    ITaskCoordinator taskCoordinator,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
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

        // Railway chain: Build context -> Execute tasks -> Apply changes -> Persist
        var scriptContext = await BuildScriptContextAsync(context, cancellationToken);
        
        var executeResult = await ExecuteTasksAsync(context, scriptContext, cancellationToken);
        if (!executeResult.IsSuccess)
        {
            return Result<StepOutcome>.Fail(executeResult.Error);
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
    /// Executes the OnExecute tasks and returns Result for error propagation.
    /// Error boundary handling is applied transparently via TaskExecutorInvoker.
    /// </summary>
    private async Task<Result> ExecuteTasksAsync(
        TransitionExecutionContext context,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        var instanceTransitionId = GetTransitionRecordId(context);

        // Set transition context for executor invoker (error boundary resolution)
        TaskExecutorInvoker.SetTransitionContext(context);
        try
        {
            return await taskCoordinator.ExecuteAsync(
                context.Transition!.OnExecutionTasks,
                instanceTransitionId,
                TaskTrigger.OnExecute,
                scriptContext,
                cancellationToken);
        }
        finally
        {
            // Clear transition context after execution
            TaskExecutorInvoker.SetTransitionContext(null);
        }
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
