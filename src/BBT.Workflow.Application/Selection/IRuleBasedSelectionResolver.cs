using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;

namespace BBT.Workflow.Selection;

/// <summary>
/// The single rule-based selection loop: entries are evaluated in declaration order, the first match
/// wins, a rule-less entry short-circuits, and a rule that cannot be evaluated is skipped rather than
/// failing the request.
/// <para>
/// Every surface that picks a component by rule - function contract slots
/// (<see cref="IFunctionContractResolver"/>) and a transition's <c>schemas</c> - goes through this one
/// object. A second matcher would let the surfaces disagree about the same definition, which is the
/// failure mode this exists to prevent.
/// </para>
/// </summary>
public interface IRuleBasedSelectionResolver
{
    /// <summary>
    /// Evaluates <paramref name="candidates"/> in declaration order and returns the first match.
    /// </summary>
    /// <param name="candidates">The declared entries, in authoring order.</param>
    /// <param name="scriptContext">
    /// The context rules are evaluated against. Materialized only when an entry actually declares a
    /// rule, so a rule-less selection never pays for building one.
    /// </param>
    /// <param name="onRuleEvaluationFailed">
    /// Invoked with the skipped entry's reference and the error message when a rule cannot be
    /// evaluated, so each caller logs through its own <c>WorkflowLogs</c> extension rather than having
    /// this loop guess at the right message.
    /// </param>
    /// <returns>
    /// <c>Ok(null)</c> when nothing was declared or every entry carried a rule and none matched - the
    /// definition declares no component for this request, which is not an error in itself. Fails only
    /// when the script context could not be built.
    /// </returns>
    Task<Result<SelectionMatch?>> ResolveAsync(
        IReadOnlyList<SelectionCandidate> candidates,
        LazyScriptContext scriptContext,
        Action<Reference, string>? onRuleEvaluationFailed = null,
        CancellationToken cancellationToken = default);
}
