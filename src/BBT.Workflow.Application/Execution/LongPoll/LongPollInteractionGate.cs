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
/// Default <see cref="ILongPollInteractionGate"/>. The rule arm builds the same script context the
/// view and notification rules run against (workflow, instance latest data, headers, query
/// parameters) and evaluates through the shared condition service, so a compiled rule is cached by
/// content hash like every other condition script; deny-on-failure is deliberate and mirrors
/// <c>StateNotifyJobHandler</c> — an interaction a broken rule cannot vouch for is not offered.
/// The roles arm delegates to the one role evaluator (<see cref="ITransitionAuthorizationManager"/>),
/// resolving the caller's roles lazily through the surface-supplied factory.
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
        var longPoll = state?.Interaction?.LongPoll;

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
            .WithBody(instance.LatestData?.Data ?? new JsonData("{}"))
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
