using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that executes the current state's OnExit tasks.
/// These tasks run when leaving the current state.
/// Uses Result pattern for exception-free error handling.
/// Error boundary handling is applied transparently via ErrorBoundaryTaskCoordinatorDecorator.
/// </summary>
public sealed class RunOnExitTasksStep(
    ITaskCoordinator taskCoordinator,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.OnExit;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(RunOnExitTasksStep)}");

        // Skip if no OnExit tasks
        if (!HasOnExitTasks(context))
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
    /// Checks if context has OnExit tasks.
    /// </summary>
    private static bool HasOnExitTasks(TransitionExecutionContext context)
        => context.Current.OnExits.Any();

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
    /// Executes the OnExit tasks and returns Result for error propagation.
    /// Error boundary handling is applied transparently via ErrorBoundaryTaskCoordinatorDecorator.
    /// </summary>
    private async Task<Result> ExecuteTasksAsync(
        TransitionExecutionContext context,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        var instanceTransitionId = GetTransitionRecordId(context);

        // Set transition context for error boundary decorator
        ErrorBoundaryTaskCoordinatorDecorator.SetTransitionContext(context);
        try
        {
            return await taskCoordinator.ExecuteAsync(
                context.Current.OnExits,
                instanceTransitionId,
                TaskTrigger.OnExit,
                scriptContext,
                cancellationToken);
        }
        finally
        {
            // Clear transition context after execution
            ErrorBoundaryTaskCoordinatorDecorator.SetTransitionContext(null);
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
        var builder = scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(context.Workflow)
            .WithInstance(context.Instance)
            .WithBody(context.Data)
            .WithRuntime(runtimeInfoProvider)
            .WithHeaders(context.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
        
        if (context.Transition != null)
            builder.WithTransition(context.Transition);
        
        return await builder.BuildAsync(cancellationToken);
    }
}
