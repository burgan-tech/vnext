using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Instances;

/// <summary>
/// Executes a workflow-level output mapping script and returns the mapped data
/// to replace the default instance attributes in a sync response.
/// </summary>
public interface IWorkflowOutputMappingService
{
    /// <summary>
    /// Compiles and runs the workflow's <see cref="Definitions.Workflow.Output"/> script
    /// using the provided <see cref="ScriptContext"/>.
    /// Returns <c>null</c> when no output script is configured or the script produces no data.
    /// Returns <see cref="Result{T}.Fail"/> when the script throws, so the caller can fall back gracefully.
    /// </summary>
    Task<Result<JsonElement?>> ApplyAsync(
        Definitions.Workflow workflow,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default);
}
