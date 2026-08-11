using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;

namespace BBT.Workflow.Selection;

/// <summary>
/// Resolves which schema component a transition points at for the current request, by evaluating its
/// rule-based <c>schemas</c> entries.
/// <para>
/// Both surfaces that need a transition's schema use this: the <c>schema</c> function, which hands the
/// resolved document to the client, and transition execution, which validates the request body against
/// it. They must agree, or a client is validated against a schema it was never shown.
/// </para>
/// </summary>
public interface ITransitionSchemaResolver
{
    /// <summary>
    /// Returns the winning schema reference, or <c>Ok(null)</c> when the transition declares no schema
    /// or every entry carried a rule and none matched. Fails only when the script context could not be
    /// built.
    /// </summary>
    Task<Result<Reference?>> ResolveAsync(
        Transition transition,
        LazyScriptContext scriptContext,
        CancellationToken cancellationToken = default);
}
