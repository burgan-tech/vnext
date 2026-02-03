using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that evaluates and executes automatic transitions.
/// Evaluates all automatic transition conditions and requests the first satisfied transition
/// to be executed via sync dispatch chain.
/// </summary>
public sealed class RunAutomaticTransitionsStep(
    IAutoConditionEvaluator autoConditionEvaluator,
    ILogger<RunAutomaticTransitionsStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Auto;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(RunAutomaticTransitionsStep)}");

        var nonBlockingFailures = NonBlockingTaskFailures.Get(context);

        // Check if target state has any automatic transitions
        if (context.Target?.AutoTransitions == null || !context.Target.AutoTransitions.Any())
        {
            if (nonBlockingFailures.Count > 0)
            {
                return Result<StepOutcome>.Fail(
                    ExecutionErrors.UnhandledNonBlockingTaskFailures(
                        context.TransitionKey,
                        context.Target?.Key ?? context.Current.Key,
                        nonBlockingFailures,
                        reason: "NoAutoTransitions"));
            }

            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Evaluate all transitions -> Process winner (if exists)
        var evalResult = await EvaluateAllTransitionsAsync(context, cancellationToken);
        if (!evalResult.IsSuccess)
        {
            return Result<StepOutcome>.Fail(evalResult.Error);
        }

        return ProcessEvaluationResults(context, evalResult.Value!, nonBlockingFailures);
    }

    /// <summary>
    /// Processes evaluation results and requests the winning transition for sync dispatch.
    /// If a winner is found, requests next transition and skips to Finalize step.
    /// </summary>
    private Result<StepOutcome> ProcessEvaluationResults(
        TransitionExecutionContext context,
        List<AutoConditionEvaluation> evaluations,
        IReadOnlyList<NonBlockingTaskFailure> nonBlockingFailures)
    {
        var winner = evaluations.FirstOrDefault(e => e.Status == AutoConditionStatus.Satisfied);

        if (winner.TransitionKey is null)
        {
            logger.AutoTransitionConditionNotSatisfied(
                context.Target!.Key,
                context.InstanceId,
                string.Join(", ", evaluations.Select(e => e.TransitionKey)));

            if (nonBlockingFailures.Count > 0)
            {
                return Result<StepOutcome>.Fail(
                    ExecutionErrors.UnhandledNonBlockingTaskFailures(
                        context.TransitionKey,
                        context.Target!.Key,
                        nonBlockingFailures,
                        reason: "NoAutoTransitionWinner"));
            }

            // No winner found - continue pipeline, stay in current state
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        logger.AutoTransitionSelected(winner.TransitionKey, context.Target!.Key, context.InstanceId);
        
        // Request next transition for sync dispatch chain
        context.Directives.RequestNextTransition(
            new NextTransitionRequest(winner.TransitionKey, "auto"));

        // Skip to Finalize to complete current transition, then pipeline will chain to next
        // return StepOutcome.SkipTo(LifecycleOrder.Finalize);
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    /// <summary>
    /// Evaluates all automatic transitions and returns the results.
    /// </summary>
    private async Task<Result<List<AutoConditionEvaluation>>> EvaluateAllTransitionsAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var evaluations = new List<AutoConditionEvaluation>();
        var orderedTransitions = context.Target!.AutoTransitions.OrderBy(t => t.TriggerKind);

        foreach (var automaticTransition in orderedTransitions)
        {
            var evalResult = await autoConditionEvaluator.EvaluateAsync(
                automaticTransition,
                context,
                cancellationToken);

            if (!evalResult.IsSuccess)
            {
                return Result<List<AutoConditionEvaluation>>.Fail(evalResult.Error);
            }

            evaluations.Add(evalResult.Value);
            if (evalResult.Value.Status == AutoConditionStatus.Satisfied)
            {
                break;
            }
        }

        return Result<List<AutoConditionEvaluation>>.Ok(evaluations);
    }
}
