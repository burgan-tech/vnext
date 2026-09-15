using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.LongPoll;

/// <summary>
/// Decides whether one caller is admitted to a state's <c>interaction.longPoll</c> — owning the
/// whole arm selection: the condition <c>rule</c> when one is declared, otherwise the <c>roles</c>
/// grants (default-allow when neither is authored; the workflow validator rejects both). Both
/// surfaces that enforce the interaction (the State function's signal emit and the acknowledge
/// endpoint) MUST admit through this single gate so their verdicts cannot diverge.
/// </summary>
public interface ILongPollInteractionGate
{
    /// <summary>
    /// Returns <c>Ok(true)</c> when the caller is admitted, <c>Ok(false)</c> when denied — a rule
    /// returning false, throwing, or failing to compile denies (fail-closed, logged; the
    /// fallback-timeout job resumes the pipeline regardless, so a broken rule cannot strand the
    /// instance) — and <c>Fail</c> only when <paramref name="callerRolesFactory"/> fails, so the
    /// surface can propagate the role-resolution error rather than masking it as a deny.
    /// </summary>
    /// <param name="instance">The polled/acknowledged instance, exposed to the rule as <c>context.Instance</c> (data read lazily via <c>context.Instance.Data</c>; <c>context.Body</c> is not populated on this surface).</param>
    /// <param name="workflow">The instance's workflow definition (flow-level scripts for compilation).</param>
    /// <param name="state">The state whose interaction gates the caller; null or no interaction ⇒ admitted.</param>
    /// <param name="headers">Request headers — exposed to the rule's script context and to dynamic role grants.</param>
    /// <param name="queryParameters">Request query parameters, when the surface carries them (the acknowledge endpoint does not).</param>
    /// <param name="callerRolesFactory">
    /// Resolves the caller's role set — invoked only when the roles arm applies, so a rule-gated
    /// state never pays the surface's (possibly remote) role resolution. Each surface keeps its own
    /// resolution policy: the State function passes its pre-resolved input roles; the acknowledge
    /// endpoint composes its additive explicit-role + provider set.
    /// </param>
    /// <param name="surface">"state" or "ack" — names the enforcing surface in the deny log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<bool>> IsAdmittedAsync(
        Instance instance,
        Definitions.Workflow workflow,
        State? state,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        Func<CancellationToken, Task<Result<IReadOnlyCollection<string>>>> callerRolesFactory,
        string surface,
        CancellationToken cancellationToken = default);
}
