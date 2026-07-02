using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Instances;

/// <summary>
/// Result of a workflow output mapping script: the mapped payload plus optional
/// HTTP status code and headers. <see cref="Data"/> may be null (intentional empty response).
/// </summary>
public sealed record WorkflowOutputResult(
    object? Data,
    int? StatusCode,
    Dictionary<string, string>? Headers);

/// <summary>
/// Executes a workflow-level output mapping script and returns the mapped payload
/// to be returned directly as the sync HTTP response body (bypassing the standard envelope).
/// </summary>
public interface IWorkflowOutputMappingService
{
    /// <summary>
    /// Compiles and runs the workflow's <see cref="Definitions.Workflow.Output"/> script
    /// using the provided <see cref="ScriptContext"/>.
    /// Returns <c>null</c> when no output script is configured (or on script failure, logged
    /// non-blocking) so the caller keeps the standard envelope. When the script runs, returns a
    /// <see cref="WorkflowOutputResult"/> whose <see cref="WorkflowOutputResult.Data"/> may be null.
    /// </summary>
    Task<Result<WorkflowOutputResult?>> ApplyAsync(
        Definitions.Workflow workflow,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default);
}
