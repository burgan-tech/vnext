using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Applies master schema field-level visibility filtering to instance data based on effective caller roles.
/// </summary>
public interface ISchemaFieldFilterService
{
    /// <summary>
    /// Filters the given JSON data by the caller's visible fields according to the workflow's master schema role grants.
    /// Returns filtered <see cref="JsonElement"/> or the original data if no schema or no role grants are defined.
    /// When <paramref name="instance"/> is provided, also evaluates $InstanceStarter and $PreviousUser predefined roles.
    /// </summary>
    Task<JsonElement?> ApplyAsync(
        Definitions.Workflow? workflow,
        JsonElement? data,
        Instance? instance = null,
        CancellationToken cancellationToken = default);
}
