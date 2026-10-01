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
    /// <c>x-roles</c> grants and transforms it per <c>x-masking</c> / <c>x-encryption</c>. Returns the filtered
    /// <see cref="JsonElement"/>, or <c>null</c> when nothing was applied (no schema, the schema could not be read, no
    /// rules, non-object data). Never call it directly from a read surface: <see cref="Instances.IInstanceDataReadService"/>
    /// owns the fallback, which is the STORED form — falling back to the plaintext input would serve encrypt values in clear.
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
    /// <param name="storedTokens">
    /// Stored token per opened path of the row <paramref name="data"/> was read from (<see cref="InstanceDataView.Tokens"/>). Required for
    /// <c>x-encryption.type: "encrypt"</c> fields to serve their stored token to non-exempt callers; without it
    /// those callers get a full mask (never the plaintext).
    /// </param>
    Task<JsonElement?> ApplyAsync(
        Definitions.Workflow? workflow,
        JsonElement? data,
        Instance? instance = null,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? storedTokens = null);
}

/// <summary>Creates an isolated list operation that reuses schema metadata, never visibility decisions.</summary>
public interface IListSchemaFieldFilterFactory
{
    /// <summary>Returns a metadata-reusing filter owned by one list invocation.</summary>
    ISchemaFieldFilterService CreateForList();
}
