using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Execution.Transitions.Pipeline;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Orchestrates the execution of transition lifecycle steps in a deterministic order.
/// Each step in the pipeline performs a specific operation during the transition.
/// Uses Result pattern for exception-free error handling.
/// Supports error boundary handling for State and Global level policies via step interceptor.
/// </summary>
public class TransitionPipeline
{
    private readonly IReadOnlyList<ITransitionStep> _steps;
    private readonly ErrorBoundaryStepInterceptor _stepInterceptor;

    /// <summary>
    /// Initializes a new instance of the TransitionPipeline.
    /// </summary>
    /// <param name="steps">The collection of pipeline steps to execute.</param>
    /// <param name="stepInterceptor">Error boundary step interceptor for step-level error handling.</param>
    public TransitionPipeline(
        IEnumerable<ITransitionStep> steps,
        ErrorBoundaryStepInterceptor stepInterceptor)
    {
        _steps = steps.OrderBy(s => s.Order).ToList();
        _stepInterceptor = stepInterceptor;
    }

    /// <summary>
    /// Executes all pipeline steps in order for the given transition context.
    /// Stateful loop with flow control (stop, replan, continue).
    /// Returns Result to indicate success or failure without throwing exceptions.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A Result indicating success or failure of the pipeline execution.</returns>
    [Trace]
    public async Task<Result> RunAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        var state = CreateInitialState(context);

        while (state.HasMoreSteps())
        {
            // Guard: Skip immediate execution requested
            if (context.SkipImmediateExecution)
                return Result.Ok();

            // Execute current step
            var stepResult = await ExecuteStepAsync(state.CurrentStep, context, cancellationToken);
            if (!stepResult.IsSuccess)
                return Result.Fail(stepResult.Error);

            // Determine flow control based on step outcome
            var flowControl = DetermineFlowControl(stepResult.Value!, state.CurrentStep, context, state);

            // Apply flow control decision
            if (flowControl.ShouldStop)
                break;

            if (flowControl.ShouldReplan)
            {
                state = CreateInitialState(context);
                continue;
            }

            state = state.MoveNext();
        }

        return Result.Ok();
    }

    /// <summary>
    /// Creates initial pipeline state with execution plan.
    /// </summary>
    private PipelineState CreateInitialState(TransitionExecutionContext context)
        => new(BuildExecutionPlan(context), 0);

    /// <summary>
    /// Builds an execution plan by filtering and ordering steps based on context directives.
    /// Handles resume points, epilogue modes, and terminal states.
    /// </summary>
    private IReadOnlyList<ITransitionStep> BuildExecutionPlan(TransitionExecutionContext context)
    {
        var ordered = _steps.ToList();

        // 1) ResumeFrom start
        var startOrder = context.Directives.ConsumeResumeFrom();
        if (startOrder.HasValue)
            ordered = ordered.Where(s => s.Order >= startOrder.Value).ToList();

        // 2) Subflow terminal short circuit (until Finalize)
        if (context.Directives.TerminalReached)
        {
            var maxOrder = LifecycleOrder.Finalize;
            ordered = ordered.Where(s => s.Order <= maxOrder).ToList();
        }

        // 3) Epilogue policy
        if (context.Directives.Epilogue == EpilogueMode.Skip)
        {
            ordered = ordered
                .Where(s => s.Order != LifecycleOrder.Schedule &&
                            s.Order != LifecycleOrder.Auto)
                .ToList();
        }

        return ordered;
    }

    /// <summary>
    /// Executes a single pipeline step through the error boundary interceptor.
    /// </summary>
    private Task<Result<StepOutcome>> ExecuteStepAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        return _stepInterceptor.ExecuteWithErrorBoundaryAsync(step, context, cancellationToken);
    }

    /// <summary>
    /// Determines flow control based on step outcome.
    /// Applies directive mutations and returns appropriate flow control decision.
    /// Sync method - no async operations needed.
    /// </summary>
    private static FlowControl DetermineFlowControl(
        StepOutcome outcome,
        ITransitionStep step,
        TransitionExecutionContext context,
        PipelineState state)
    {
        // Apply directive mutations from outcome
        outcome.MutateDirectives?.Invoke(context.Directives);

        // 0) Check for error boundary transition request
        if (outcome.ErrorActionResult != null && outcome.ErrorActionResult.HasTransition)
        {
            // Error boundary requested a transition - store in Items for post-pipeline handling
            context.Items["ErrorBoundary:TransitionKey"] = outcome.ErrorActionResult.TransitionKey;
        }

        // 1) Stop for retry? (graceful stop - retry job is scheduled)
        if (outcome.StopForRetry)
        {
            context.Items["Pipeline:StoppedForRetry"] = true;
            return FlowControl.Stop();
        }

        // 2) Stop pipeline?
        if (outcome.StopPipeline)
            return FlowControl.Stop();

        // 3) Skip to specific order? (e.g., restart from CreateTransition after inline auto)
        if (outcome.SkipToOrder is { } skipTo)
        {
            context.Directives.RequestResumeFrom(skipTo);
            return FlowControl.Replan();
        }

        // 4) Directives changed requiring replan?
        if (NeedsReplan(state.Plan, context.Directives))
        {
            context.Directives.RequestResumeFrom(step.Order + 1);
            return FlowControl.Replan();
        }

        // Continue to next step
        return FlowControl.Continue();
    }

    /// <summary>
    /// Determines if the execution plan needs to be rebuilt.
    /// Checks for terminal state, epilogue mode changes, and resume requests.
    /// </summary>
    private static bool NeedsReplan(IReadOnlyList<ITransitionStep> currentPlan, PipelineDirectives d)
    {
        if (d.TerminalReached)
            return true;

        if (d.Epilogue == EpilogueMode.Skip &&
            currentPlan.Any(s => s.Order == LifecycleOrder.Schedule || s.Order == LifecycleOrder.Auto))
            return true;

        if (d.ResumeFromOrder is not null)
            return true;

        return false;
    }

    /// <summary>
    /// Represents the current execution state of the pipeline.
    /// Immutable record struct for functional state management.
    /// </summary>
    private readonly record struct PipelineState(IReadOnlyList<ITransitionStep> Plan, int Index)
    {
        public ITransitionStep CurrentStep => Plan[Index];
        public bool HasMoreSteps() => Index < Plan.Count;
        public PipelineState MoveNext() => this with { Index = Index + 1 };
    }

    /// <summary>
    /// Represents flow control decision after step execution.
    /// Factory methods provide clear intent.
    /// </summary>
    private readonly record struct FlowControl(bool ShouldStop, bool ShouldReplan)
    {
        public static FlowControl Stop() => new(ShouldStop: true, ShouldReplan: false);
        public static FlowControl Replan() => new(ShouldStop: false, ShouldReplan: true);
        public static FlowControl Continue() => new(ShouldStop: false, ShouldReplan: false);
    }
}