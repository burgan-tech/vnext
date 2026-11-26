using BBT.Aether.Results;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Instances;

/// <summary>
/// Result of a workflow output mapping (script or declarative block): the mapped payload plus optional
/// HTTP status code and headers. <see cref="Data"/> may be null (intentional empty response).
/// </summary>
public sealed record WorkflowOutputResult(
    object? Data,
    int? StatusCode,
    Dictionary<string, string>? Headers);

/// <summary>
/// The hop a sync response answers, as a declarative output block sees it.
/// </summary>
/// <param name="Input">The hop's request payload (start attributes or transition body) — the block's <c>input</c>.</param>
/// <param name="Actor">The hop's actor; with <paramref name="Headers"/> it resolves the block's <c>user</c>.</param>
/// <param name="Headers">The hop's request headers.</param>
public sealed record WorkflowOutputHop(
    object? Input,
    ExecutionActor Actor,
    IReadOnlyDictionary<string, string?>? Headers);

/// <summary>
/// Executes a workflow-level output mapping — a script or a declarative block — and returns the mapped payload
/// to be returned directly as the sync HTTP response body (bypassing the standard envelope).
/// </summary>
public interface IWorkflowOutputMappingService
{
    /// <summary>
    /// Runs the workflow's <see cref="Definitions.Workflow.Output"/> script or, when it has none, its
    /// <see cref="Definitions.Workflow.OutputBlock"/> using the provided <see cref="ScriptContext"/>.
    /// Returns <c>null</c> when no output is configured (or on failure, logged non-blocking) so the caller keeps
    /// the standard envelope. When the output runs, returns a <see cref="WorkflowOutputResult"/> whose
    /// <see cref="WorkflowOutputResult.Data"/> may be null. <paramref name="hop"/> backs a block's <c>input</c> and
    /// <c>user</c>; a script does not read it.
    /// </summary>
    Task<Result<WorkflowOutputResult?>> ApplyAsync(
        Definitions.Workflow workflow,
        ScriptContext scriptContext,
        WorkflowOutputHop? hop,
        CancellationToken cancellationToken = default);
}
