using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Default <see cref="ILongPollInteractionGate"/>. The rule arm builds the same script context shape
/// the view and notification rules run against (workflow, instance, headers, query parameters) and
/// evaluates through the shared condition service, so a compiled rule is cached by content hash like
/// every other condition script. One deliberate difference from the view-rule context:
/// <c>context.Body</c> is NOT populated — this surface has no request payload, and pre-materializing
/// the latest instance data into Body costs a full serialize+parse per evaluation (per poll, since
/// rule-gated bodies skip the shared state body cache) even for rules that never read data. Rules
/// read instance data through the lazy, memoized <c>context.Instance.Data</c> instead.
/// Deny-on-failure is deliberate and mirrors <c>StateNotifyJobHandler</c> — an interaction a broken
/// rule cannot vouch for is not offered. The roles arm delegates to the one role evaluator
/// (<see cref="ITransitionAuthorizationManager"/>), resolving the caller's roles lazily through the
/// surface-supplied factory. Arm selection reads <c>Instance.ResolveEffectiveLongPoll</c>, so a
/// parent's roles override applies on every surface that admits through this gate.
/// </summary>
public sealed class LongPollInteractionGate(
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    ITaskConditionService taskConditionService,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    ILogger<LongPollInteractionGate> logger) : ILongPollInteractionGate
{
    /// <inheritdoc />
    public async Task<Result<bool>> IsAdmittedAsync(
        Instance instance,
        Definitions.Workflow workflow,
        State? state,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>> callerRolesFactory,
        string surface,
        CancellationToken cancellationToken = default)
    {
        // The effective long-poll: the state's own declaration with the parent's field-level override
        // (roles, window) applied. Rule arm stays the child author's — an override never replaces it.
        var longPoll = instance.ResolveEffectiveLongPoll(state).LongPoll;

        if (longPoll?.Rule is { } rule)
        {
            var admitted = await EvaluateRuleAsync(
                rule, instance, workflow, state!, headers, queryParameters, surface, cancellationToken);
            return Result<bool>.Ok(admitted);
        }

        if (longPoll?.Roles is { Count: > 0 } roleGrants)
        {
            var callerRoles = await callerRolesFactory(cancellationToken);
            if (!callerRoles.IsSuccess)
                return Result<bool>.Fail(callerRoles.Error);

            var allowed = await transitionAuthorizationManager.IsAnyRoleAllowedForGrantsAsync(
                callerRoles.Value, roleGrants, instance,
                new AuthorizationRequestContext(headers, queryParameters), cancellationToken);
            return Result<bool>.Ok(allowed);
        }

        return Result<bool>.Ok(true);
    }

    private async Task<bool> EvaluateRuleAsync(
        ScriptCode rule,
        Instance instance,
        Definitions.Workflow workflow,
        State state,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        string surface,
        CancellationToken cancellationToken)
    {
        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(workflow)
            .WithInstance(instance)
            .WithRuntime(runtimeInfoProvider)
            .WithTransition(string.Empty)
            .WithHeaders(headers)
            .WithQueryParameters(queryParameters)
            .BuildAsync(cancellationToken);

        var ruleResult = await taskConditionService.ExecuteConditionAsync(rule, scriptContext, cancellationToken);
        if (!ruleResult.IsSuccess)
        {
            logger.LongPollInteractionRuleEvaluationFailed(
                surface, instance.Id, state.Key, ruleResult.Error.Message ?? ruleResult.Error.Code);
        }

        return ruleResult is { IsSuccess: true, Value: true };
    }
}
