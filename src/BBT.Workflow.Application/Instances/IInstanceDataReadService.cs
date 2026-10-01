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
