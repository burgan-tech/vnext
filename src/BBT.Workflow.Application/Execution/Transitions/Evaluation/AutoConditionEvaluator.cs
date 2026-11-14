using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Authorization;
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
    IRuntimeInfoProvider runtimeInfoProvider,
    ICallerAccessorFactory? callerAccessorFactory = null) : IAutoConditionEvaluator
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
        // AiAgent state (issue #1105): a rule-less automatic transition is decided by the agent run of
        // THIS hop, never by a rule and never by default. Without an in-hop decision (updateData, a resume
        // path) it is simply not satisfied — the agent's fallback must not fire because no agent ran.
        if (context.Target is { StateType: StateType.AiAgent } agentState &&
            AgentCandidateSet.IsAgentDecided(agentState, transition))
        {
            return Task.FromResult(Result<AutoConditionEvaluation>.Ok(EvaluateAgentDecided(transition, context.AgentDecision)));
        }

        if(transition.TriggerKind == TransitionKind.DefaultAutoTransition)
        {
            return Task.FromResult(Result<AutoConditionEvaluation>.Ok(AutoConditionEvaluation.Satisfied(transition.Key)));
        }

        return ValidateTransitionRule(transition)
            .BindAsync(_ => ExecuteConditionSafelyAsync(transition, context, cancellationToken));
    }

    /// <summary>
    /// Evaluates a transition the agent decides. Model and script modes: satisfied only for the selected
    /// key. Rules mode after a completed run: the candidates carry rules, so only the fallback is
    /// agent-decided and it is satisfied (it is evaluated last, after every rule).
    /// </summary>
    private static AutoConditionEvaluation EvaluateAgentDecided(Transition transition, BBT.Workflow.Instances.Agents.AgentHopDecision? decision)
    {
        if (decision is null)
            return new AutoConditionEvaluation(transition.Key, AutoConditionStatus.NotSatisfied);

        if (decision.Source == BBT.Workflow.Instances.Agents.AgentDecisionSource.Rules)
        {
            return string.Equals(transition.Key, decision.Fallback, StringComparison.Ordinal)
                ? AutoConditionEvaluation.Satisfied(transition.Key)
                : new AutoConditionEvaluation(transition.Key, AutoConditionStatus.NotSatisfied);
        }

        return string.Equals(transition.Key, decision.Selected, StringComparison.Ordinal)
            ? AutoConditionEvaluation.Satisfied(transition.Key)
            : new AutoConditionEvaluation(transition.Key, AutoConditionStatus.NotSatisfied);
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
                // NOTE: this call site is invoked once per candidate transition by
                // Step.RunAutomaticTransitions, so every memo hit here increments the SAME
                // enclosing span's counter tag via Activity.IncrementCounterTag (unsynchronized
                // read-modify-write — sound today because this loop runs single-flow). If
                // auto-transition evaluation is ever parallelized, this is the site that would
                // race: count hits locally per branch and set the tag once at the join, per the
                // rule already documented on IncrementCounterTag itself.
                var scriptContext = await context.GetOrBuildScriptContextAsync(
                    innerCt => CreateScriptContextAsync(context, innerCt),
                    ct);

                // Caller roles (context.Caller) — decided in council 2026-11-12-script-caller-roles-background
                // (Chair override): automatic-transition rules see the roles of the caller who drove this hop
                // (the request, the async-accept job replaying its headers, the auto hops chained inline after
                // them). System hops (a fired timer, a SubFlow resume, a continuation) have no caller and get the
                // unavailable accessor. The context is SHARED with this hop's OnEntry/OnExit/OnExecute/ResourceLock
                // task mappings, so the caller is exposed for this rule only and closed afterwards.
                var caller = callerAccessorFactory?.ForHop(context.Actor, context.Headers, ct)
                             ?? NullCallerAccessor.Instance;
                var conditionResult = await ExecuteWithCallerAsync(transition.Rule!, scriptContext, caller, ct);

                // A rule that read the caller's roles while the provider could not answer decides nothing,
                // whatever it returned, threw or swallowed: abandon the whole evaluation (the DefaultAutoTransition
                // included) instead of routing the instance on an unidentified caller. Retryable 110006; the step
                // fails through the error boundary. (A rule that throws for any OTHER reason still reads as
                // "not satisfied" here — a pre-existing behaviour whose fix is a separate, announced change.)
                if (caller.ResolutionFailed)
                    throw new CallerRolesUnavailableForAutoRuleException(transition.Key);

                return MapToEvaluation(transition.Key, conditionResult.Value);
            },
            cancellationToken,
            ex => CreateScriptExecutionError(transition, context, ex));
    }

    /// <summary>
    /// Maps the boolean condition result to an AutoConditionEvaluation.
    /// Pure transformation function.
    /// </summary>
    private async Task<Result<bool>> ExecuteWithCallerAsync(
        ScriptCode rule, ScriptContext scriptContext, ICallerAccessor caller, CancellationToken cancellationToken)
    {
        using (scriptContext.ExposeCaller(caller))
        {
            return await taskConditionService.ExecuteConditionAsync(rule, scriptContext, cancellationToken);
        }
    }

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
        // Already logged (20519) by the accessor factory with the provider's failure kind.
        if (ex is CallerRolesUnavailableForAutoRuleException)
            return WorkflowErrors.CallerRolesUnavailable();

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

/// <summary>Raised inside the guarded evaluation so the TryAsync wrapper maps it to <c>Authorization:110006</c>.</summary>
public sealed class CallerRolesUnavailableForAutoRuleException(string transitionKey)
    : Exception($"Caller roles could not be resolved for automatic transition '{transitionKey}'.");
