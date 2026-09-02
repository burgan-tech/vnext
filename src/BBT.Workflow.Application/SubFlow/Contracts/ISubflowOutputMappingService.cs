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
    /// Resolves the parent state and compiles its output mapping without touching mutable instance
    /// state. This preparation can safely run before the terminal correlation lock is acquired.
    /// </summary>
    Task<Result<SubflowOutputMappingPlan>> PrepareAsync(
        Guid parentInstanceId,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        CancellationToken cancellationToken = default);

    /// <summary>Executes a previously prepared mapping against authoritative locked state.</summary>
    Task<Result> ApplyPreparedAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        SubflowOutputMappingPlan plan,
        JsonElement? childInstanceData,
        CancellationToken cancellationToken = default);

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

public sealed record SubflowOutputMappingPlan(State? ParentState, object? MappingInstance);
