using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that evaluates and executes automatic transitions.
/// Evaluates the state's automatic transitions in <c>State.AutoTransitions</c> order (priority, then
/// declaration, default last) and requests the first satisfied one to be executed via sync dispatch chain.
/// </summary>
public sealed class RunAutomaticTransitionsStep(
    IAutoConditionEvaluator autoConditionEvaluator,
    ILogger<RunAutomaticTransitionsStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Auto;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
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

            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
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
        RecordRulesDecision(context, winner.TransitionKey);
        
        // Request next transition for sync dispatch chain
        context.Directives.RequestNextTransition(
            new NextTransitionRequest(winner.TransitionKey, "auto"));

        // Skip to Finalize to complete current transition, then pipeline will chain to next
        // return StepOutcome.SkipTo(LifecycleOrder.Finalize);
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    /// <summary>
    /// The automatic transitions in evaluation order. On an <see cref="Definitions.StateType.AiAgent"/> state
    /// with an in-hop decision (issue #1105): a decision that is a reserved exit (fallback after a failed run,
    /// onExhausted) is taken alone, without evaluating any rule against data the agent never produced;
    /// otherwise rule-bearing transitions and candidates keep their order and the reserved exits go last.
    /// </summary>
    private static IEnumerable<Definitions.Transition> OrderForAgent(TransitionExecutionContext context)
    {
        var all = context.Target!.AutoTransitions;
        if (context.Target.StateType != Definitions.StateType.AiAgent || context.AgentDecision is not { } decision)
            return all;

        if (decision.IsReservedExit && decision.Source != BBT.Workflow.Instances.Agents.AgentDecisionSource.Rules)
            return all.Where(t => t.Key == decision.Selected);

        return all.Where(t => !decision.ReservedKeys.Contains(t.Key))
            .Concat(all.Where(t => decision.ReservedKeys.Contains(t.Key)));
    }

    /// <summary>
    /// Rules mode (issue #1105): the winner is only known here, so the decision row — still pending in the
    /// hop's unit of work — gets it now and is written with the same commit.
    /// </summary>
    private void RecordRulesDecision(TransitionExecutionContext context, string winner)
    {
        if (context.AgentDecision is not { Source: BBT.Workflow.Instances.Agents.AgentDecisionSource.Rules, DecisionRow: { } row } decision)
            return;

        row.SelectedTransition = winner;
        if (string.Equals(winner, decision.Fallback, StringComparison.Ordinal))
        {
            row.Source = BBT.Workflow.Instances.Agents.AgentDecisionSource.Fallback;
            row.IsFallback = true;
        }

        logger.AgentRulesDecisionRecorded(context.InstanceId, context.Target!.Key, winner);
    }

    /// <summary>
    /// Evaluates all automatic transitions and returns the results.
    /// </summary>
    private async Task<Result<List<AutoConditionEvaluation>>> EvaluateAllTransitionsAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var evaluations = new List<AutoConditionEvaluation>();

        // State.AutoTransitions is already in evaluation order; do not re-sort here — except on an
        // AiAgent state, where OrderForAgent moves the agent's reserved exits last.
        foreach (var automaticTransition in OrderForAgent(context))
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
