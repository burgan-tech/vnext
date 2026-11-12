using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution.Validation;

/// <summary>
/// Resolves which schema a transition's payload is validated against. The single resolver shared by
/// the write path (transition validation) and the read path (schema function), so both always agree.
/// </summary>
public interface ITransitionSchemaResolver
{
    /// <summary>
    /// Resolves the schema for <paramref name="transition"/>.
    /// <c>Ok(null)</c> means the transition declares no schema (accept);
    /// <c>Ok(reference)</c> is the schema to validate against;
    /// a <c>Transition:100025</c> failure means schemas are declared but none matched.
    /// </summary>
    /// <param name="workflow">The workflow definition.</param>
    /// <param name="instance">The instance; rules see its latest data (or <c>{}</c> when it has none yet).</param>
    /// <param name="transition">The transition being executed or described.</param>
    /// <param name="headers">Request headers exposed to rules. No query parameters, no request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<Reference?>> ResolveAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default);
}
