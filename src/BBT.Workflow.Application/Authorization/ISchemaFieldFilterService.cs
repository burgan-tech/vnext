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
    /// Filters the given JSON data by the caller's visible fields according to the workflow's master schema
    /// <c>x-roles</c> grants. Returns filtered <see cref="JsonElement"/> or the original data if no schema
    /// or no role grants are defined.
    /// </summary>
    /// <param name="workflow">The workflow whose master schema carries the field grants.</param>
    /// <param name="data">The instance data to filter.</param>
    /// <param name="instance">
    /// Optional instance. Required for predefined ($InstanceStarter, $PreviousUser, …) and dynamic
    /// (<c>$.context.Instance.*</c>) grants to resolve; without it only static grants can match.
    /// </param>
    /// <param name="requestContext">
    /// Optional request context. Supplies the <c>$.context.Headers/QueryParameters/RouteValues</c> namespaces
    /// for dynamic grants, and the headers used to resolve the caller's roles (legacy <c>role</c> header).
    /// Pass the same context the surrounding read was authorized with.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<JsonElement?> ApplyAsync(
        Definitions.Workflow? workflow,
        JsonElement? data,
        Instance? instance = null,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);
}
