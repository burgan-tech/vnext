using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Default <see cref="ILongPollRuleGate"/>: builds the same script context the view and
/// notification rules run against (workflow, instance latest data, headers, query parameters) and
/// evaluates the rule through the shared condition service, so a compiled rule is cached by content
/// hash like every other condition script. Deny-on-failure is deliberate and mirrors
/// <c>StateNotifyJobHandler</c>: an interaction a broken rule cannot vouch for is not offered.
/// </summary>
public sealed class LongPollRuleGate(
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    ITaskConditionService taskConditionService,
    ILogger<LongPollRuleGate> logger) : ILongPollRuleGate
{
    /// <inheritdoc />
    public async Task<bool> IsAdmittedAsync(
        ScriptCode rule,
        Instance instance,
        Definitions.Workflow workflow,
        State state,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        string surface,
        CancellationToken cancellationToken = default)
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
