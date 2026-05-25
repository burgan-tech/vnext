using System.Text.Json;
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
    /// </summary>
    Task ApplyAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        JsonElement? childInstanceData,
        CancellationToken cancellationToken = default);
}
