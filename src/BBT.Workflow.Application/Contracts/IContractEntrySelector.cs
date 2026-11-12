using BBT.Aether.Results;
using BBT.Workflow.Functions.Contracts;

namespace BBT.Workflow.Contracts;

/// <summary>
/// Picks the winning entry of a rule-based contract slot: declaration order, first match wins, a
/// rule-less entry short-circuits, and a rule that cannot be evaluated is skipped rather than fatal.
/// Shared by function contracts and transition schemas.
/// </summary>
public interface IContractEntrySelector
{
    /// <summary>
    /// Selects the winning candidate. Returns <c>Ok(null)</c> when <paramref name="candidates"/> is empty
    /// or every candidate carried a rule and none matched (a throwing rule counts as no match). Fails only
    /// when the lazy script context cannot be built.
    /// </summary>
    /// <param name="candidates">Entries in declaration order.</param>
    /// <param name="scriptContext">Built at most once, and only when a rule must be evaluated.</param>
    /// <param name="onRuleError">Invoked with the candidate and error message when a rule fails to evaluate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<ContractSelection?>> SelectAsync(
        IReadOnlyList<ContractCandidate> candidates,
        LazyScriptContext scriptContext,
        Action<ContractCandidate, string>? onRuleError,
        CancellationToken cancellationToken = default);
}
