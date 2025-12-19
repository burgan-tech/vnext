using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution;

/// <summary>
/// Evaluates automatic transition conditions using the scripting engine.
/// Handles condition evaluation errors gracefully using the Result pattern.
/// </summary>
public sealed class AutoConditionEvaluator(
    ITaskConditionService taskConditionService,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    ILogger<AutoConditionEvaluator> logger,
    IRuntimeInfoProvider runtimeInfoProvider) : IAutoConditionEvaluator
{
    /// <inheritdoc />
    /// <summary>
    /// Evaluates the automatic transition condition.
    /// Railway chain: Validate Rule → Execute Script → Map to Evaluation
    /// </summary>
    public Task<Result<AutoConditionEvaluation>> EvaluateAsync(
        Transition transition,
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if(transition.TriggerKind == TransitionKind.DefaultAutoTransition)
        {
            return Task.FromResult(Result<AutoConditionEvaluation>.Ok(AutoConditionEvaluation.Satisfied(transition.Key)));
        }

        return ValidateTransitionRule(transition)
            .BindAsync(_ => ExecuteConditionSafelyAsync(transition, context, cancellationToken));
    }

    /// <summary>
    /// Validates that the transition has a rule defined.
    /// Logs warning and returns validation error if rule is missing.
    /// </summary>
    private Result<Transition> ValidateTransitionRule(Transition transition)
    {
        if (transition.Rule is not null)
            return Result<Transition>.Ok(transition);

        logger.AutoTransitionNoRule(transition.Key);

        return Result<Transition>.Fail(
            ExecutionErrors.AutoTransitionNoRuleDefined(transition.Key));
    }

    /// <summary>
    /// Executes the condition script safely using TryAsync.
    /// Script execution is dynamic invocation - Try is appropriate per Railway pattern.
    /// </summary>
    private Task<Result<AutoConditionEvaluation>> ExecuteConditionSafelyAsync(
        Transition transition,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        return ResultExtensions.TryAsync(
            async ct =>
            {
                var scriptContext = await context.GetOrBuildScriptContextAsync(
                    innerCt => CreateScriptContextAsync(context, innerCt),
                    ct);

                var conditionResult = await taskConditionService.ExecuteConditionAsync(
                    transition.Rule!,
                    scriptContext,
                    ct);

                return MapToEvaluation(transition.Key, conditionResult.Value);
            },
            cancellationToken,
            ex => CreateScriptExecutionError(transition, context, ex));
    }

    /// <summary>
    /// Maps the boolean condition result to an AutoConditionEvaluation.
    /// Pure transformation function.
    /// </summary>
    private static AutoConditionEvaluation MapToEvaluation(string transitionKey, bool conditionResult)
    {
        var status = conditionResult
            ? AutoConditionStatus.Satisfied
            : AutoConditionStatus.NotSatisfied;

        return new AutoConditionEvaluation(transitionKey, status);
    }

    /// <summary>
    /// Creates an error for script execution failures.
    /// Handles logging as a side effect within the error mapper.
    /// </summary>
    private Error CreateScriptExecutionError(
        Transition transition,
        TransitionExecutionContext context,
        Exception ex)
    {
        logger.TransitionRuleFailed(transition.Key, context.InstanceId, ex.Message);

        return ExecutionErrors.TransitionRuleEvaluationFailed(transition.Key, ex.Message, ex.ToString());
    }

    /// <summary>
    /// Creates a script context for condition evaluation.
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

