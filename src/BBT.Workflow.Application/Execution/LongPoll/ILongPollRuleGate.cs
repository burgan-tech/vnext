using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Evaluates a state's <c>interaction.longPoll.rule</c> condition script for one caller — the
/// rule-based alternative to <c>interaction.longPoll.roles</c>. Both surfaces that enforce the
/// interaction (the State function's signal emit and the acknowledge endpoint) MUST admit through
/// this single gate so their verdicts cannot diverge.
/// </summary>
public interface ILongPollRuleGate
{
    /// <summary>
    /// Returns true when the rule admits the caller. A rule returning false, throwing, or failing
    /// to compile denies (fail-closed, logged) — a broken rule cannot strand the instance because
    /// the fallback-timeout job resumes the pipeline regardless of callers.
    /// </summary>
    /// <param name="rule">The condition script (<c>IConditionMapping</c>).</param>
    /// <param name="instance">The polled/acknowledged instance; its latest data feeds the script context.</param>
    /// <param name="workflow">The instance's workflow definition (flow-level scripts for compilation).</param>
    /// <param name="state">The state declaring the interaction.</param>
    /// <param name="headers">Request headers exposed to the script context.</param>
    /// <param name="queryParameters">Request query parameters exposed to the script context.</param>
    /// <param name="surface">"state" or "ack" — names the enforcing surface in the deny log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> IsAdmittedAsync(
        ScriptCode rule,
        Instance instance,
        Definitions.Workflow workflow,
        State state,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        string surface,
        CancellationToken cancellationToken = default);
}
