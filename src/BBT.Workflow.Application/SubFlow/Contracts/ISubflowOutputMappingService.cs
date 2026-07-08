using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Applies a SubFlow output mapping script to child instance data and adds the mapped result to the parent.
/// </summary>
public interface ISubflowOutputMappingService
{
    /// <summary>
    /// Executes the parent SubFlow state's <see cref="Scripting.ISubFlowMapping.OutputHandler"/> with the child data
    /// as the script body, then persists mapped data and script mutations on the parent instance.
    /// Returns <see cref="Result.Ok()"/> on success or <see cref="Result.Fail"/> when the OutputHandler throws,
    /// so callers can decide whether to fault the instance instead of silently continuing.
    /// </summary>
    Task<Result> ApplyAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        JsonElement? childInstanceData,
        CancellationToken cancellationToken = default);
}
