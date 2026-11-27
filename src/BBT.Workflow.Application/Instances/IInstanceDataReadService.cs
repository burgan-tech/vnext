using System.Text.Json;
using BBT.Workflow.Authorization;

namespace BBT.Workflow.Instances;

/// <summary>
/// The single read path for instance data served to a caller — the read-side counterpart of
/// <see cref="IInstanceDataWriteService"/>. Instance GET, instance list, the data function and the sync start/transition
/// response read through it, and so do the GetInstance / GetInstances / GetInstanceData tasks (they call those same
/// surfaces locally, or the remote domain's endpoints cross-domain).
/// <para>
/// It applies the master schema's <c>x-roles</c> → <c>x-masking</c> → <c>x-encryption</c> exposure for the caller
/// (<see cref="ISchemaFieldFilterService"/>) after preloading the row's secrets. When nothing can be applied — no schema,
/// a schema that cannot be read, no rules — the STORED form is served, never the plaintext view: an encrypt value stays a
/// token, a hashed one a digest.
/// </para>
/// </summary>
public interface IInstanceDataReadService
{
    /// <summary>Returns <paramref name="row"/>'s data as the caller may see it; <c>null</c> when there is no row.</summary>
    Task<JsonElement?> ExposeAsync(
        Definitions.Workflow? workflow,
        Instance instance,
        InstanceData? row,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A reader for one list page: schema metadata is resolved once per schema version for the whole page, and the page's
    /// secrets are preloaded in one query on the first read. Visibility decisions are still made per instance.
    /// </summary>
    IInstanceDataPageReader CreatePageReader(IReadOnlyCollection<Instance> page);

    /// <summary>
    /// A reader for the request bodies recorded on one instance's transitions (transition history). The caller's exposure
    /// plan is decided once — one role resolution, one evaluator — and applied to every body.
    /// <para>
    /// A recorded body is the plaintext request (or, for a mapped transition, the mapped instance-shaped data), so the
    /// stored form is no safe fallback here: when the plan cannot be decided — <paramref name="workflow"/> is <c>null</c>
    /// because the definition could not be loaded, or its master schema cannot be read — every non-empty body is withheld.
    /// A workflow without a master schema or without rules serves the bodies as recorded.
    /// </para>
    /// </summary>
    /// <param name="workflow">The instance's workflow definition; <c>null</c> when it could not be loaded.</param>
    /// <param name="instance">The instance whose transitions are read.</param>
    /// <param name="requestContext">The request context the read was authorized with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IInstanceHistoryBodyReader> CreateHistoryBodyReaderAsync(
        Definitions.Workflow? workflow,
        Instance instance,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken = default);
}

/// <summary>One instance's transition-body reader, created by <see cref="IInstanceDataReadService.CreateHistoryBodyReaderAsync"/>.</summary>
public interface IInstanceHistoryBodyReader
{
    /// <summary>
    /// Returns <paramref name="body"/> as the caller may see it, or <c>null</c> when it is withheld. An absent or JSON
    /// <c>null</c> body is returned as it is; a non-object body under a decided plan is withheld (its fields cannot be
    /// matched to the schema).
    /// </summary>
    JsonElement? Expose(JsonElement? body);
}

/// <summary>One list page's reader, created by <see cref="IInstanceDataReadService.CreatePageReader"/>. Not thread-safe.</summary>
public interface IInstanceDataPageReader
{
    /// <inheritdoc cref="IInstanceDataReadService.ExposeAsync"/>
    Task<JsonElement?> ExposeAsync(
        Definitions.Workflow? workflow,
        Instance instance,
        InstanceData? row,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken = default);
}
