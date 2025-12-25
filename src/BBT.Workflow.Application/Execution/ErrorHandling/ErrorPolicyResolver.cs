using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Resolves error handling policies from the Task → State → Global hierarchy.
/// Implements deterministic priority-based rule matching.
/// Uses compiled policy cache for performance.
/// </summary>
public sealed class ErrorPolicyResolver : IErrorPolicyResolver
{
    private readonly ICompiledErrorPolicyCache _policyCache;
    private readonly ILogger<ErrorPolicyResolver> _logger;

    /// <summary>
    /// Initializes a new instance of ErrorPolicyResolver.
    /// </summary>
    public ErrorPolicyResolver(
        ICompiledErrorPolicyCache policyCache,
        ILogger<ErrorPolicyResolver> logger)
    {
        _policyCache = policyCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public ResolvedPolicy? Resolve(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        OnExecuteTask? onExecuteTask = null,
        bool excludeRetry = false)
    {
        _logger.LogDebug(
            "Resolving error policy for {ExceptionType} in instance {InstanceId}, scope: {Scope}, excludeRetry: {ExcludeRetry}",
            errorContext.ExceptionTypeName,
            context.InstanceId,
            errorContext.Scope,
            excludeRetry);

        // Get cached compiled policies for this workflow
        var compiledPolicies = _policyCache.GetOrCompile(context.Workflow);

        // 1. Check Task boundary (if task execution has error boundary defined)
        if (onExecuteTask?.ErrorBoundary != null)
        {
            var taskBoundary = compiledPolicies.GetOrCompileTaskBoundary(
                onExecuteTask.Task.Key,
                onExecuteTask.ErrorBoundary);
            if (taskBoundary != null)
            {
                var matchedRule = taskBoundary.FindMatch(errorContext, excludeRetry);
                if (matchedRule != null)
                {
                    _logger.LogDebug(
                        "Matched task-level error policy: action={Action}, priority={Priority}",
                        matchedRule.Action,
                        matchedRule.EffectivePriority);
                    return ResolvedPolicy.Create(matchedRule, ErrorBoundaryLevel.Task, onExecuteTask.ErrorBoundary);
                }
            }
        }

        // 2. Check State boundary
        var currentState = context.Current;
        if (currentState != null)
        {
            var stateBoundary = compiledPolicies.GetStateBoundary(currentState.Key);
            if (stateBoundary != null)
            {
                var matchedRule = stateBoundary.FindMatch(errorContext, excludeRetry);
                if (matchedRule != null)
                {
                    _logger.LogDebug(
                        "Matched state-level error policy: state={State}, action={Action}, priority={Priority}",
                        currentState.Key,
                        matchedRule.Action,
                        matchedRule.EffectivePriority);
                    return ResolvedPolicy.Create(matchedRule, ErrorBoundaryLevel.State, currentState.ErrorBoundary);
                }
            }
        }

        // 3. Check Global boundary
        if (compiledPolicies.GlobalBoundary != null)
        {
            var matchedRule = compiledPolicies.GlobalBoundary.FindMatch(errorContext, excludeRetry);
            if (matchedRule != null)
            {
                _logger.LogDebug(
                    "Matched global-level error policy: action={Action}, priority={Priority}",
                    matchedRule.Action,
                    matchedRule.EffectivePriority);
                return ResolvedPolicy.Create(matchedRule, ErrorBoundaryLevel.Global, context.Workflow.ErrorBoundary);
            }
        }

        // 4. No matching policy found
        _logger.LogWarning(
            "No error policy found for {ExceptionType} in instance {InstanceId}. Error is unhandled.",
            errorContext.ExceptionTypeName,
            context.InstanceId);

        return null;
    }

    /// <inheritdoc />
    public ResolvedPolicy? ResolveSubFlow(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        State subFlowState)
    {
        _logger.LogDebug(
            "Resolving SubFlow error policy for state {State} in instance {InstanceId}",
            subFlowState.Key,
            context.InstanceId);

        // Get cached compiled policies
        var compiledPolicies = _policyCache.GetOrCompile(context.Workflow);

        // First check the SubFlow state's error boundary
        var stateBoundary = compiledPolicies.GetStateBoundary(subFlowState.Key);
        if (stateBoundary != null)
        {
            var matchedRule = stateBoundary.FindMatch(errorContext);
            if (matchedRule != null)
            {
                _logger.LogDebug(
                    "Matched SubFlow state error policy: state={State}, action={Action}",
                    subFlowState.Key,
                    matchedRule.Action);
                return ResolvedPolicy.Create(matchedRule, ErrorBoundaryLevel.SubFlow, subFlowState.ErrorBoundary);
            }
        }

        // Fall back to global boundary
        return Resolve(context, errorContext);
    }
}

