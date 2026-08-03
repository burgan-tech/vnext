using System.Text.Json;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Applies master schema field-level visibility filtering to instance data based on the caller's roles.
/// Extracted from InstanceQueryAppService to allow reuse in command and query paths.
/// </summary>
public sealed class SchemaFieldFilterService(
    IComponentCacheStore componentCacheStore,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    ICurrentUser currentUser) : ISchemaFieldFilterService
{
    /// <inheritdoc />
    public async Task<JsonElement?> ApplyAsync(
        Definitions.Workflow? workflow,
        JsonElement? data,
        Instance? instance = null,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (workflow?.Schema is null || !data.HasValue)
            return data;
        var element = data.GetValueOrDefault();
        if (element.ValueKind != JsonValueKind.Object)
            return data;

        var schemaResult = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        if (!schemaResult.IsSuccess)
            return data;

        var pathRoleGrants = SchemaRolesParser.ParsePropertyRoles(schemaResult.Value!.Schema);
        if (pathRoleGrants.Count == 0)
            return data;

        // Honor the legacy `role` header: a caller whose roles arrive only as a header must not be filtered
        // as if role-less. This has to match how the surrounding read was authorized and cache-keyed,
        // otherwise the same cache entry can be filled with differently-filtered bodies.
        var callerRoles = currentUser.ResolveCallerRoles(requestContext?.Headers);

        // One evaluator for the whole schema: the union of every guarded path's grants decides the single
        // prefetch, and predefined/dynamic grants are then matched on the grant side per path.
        var evaluator = await transitionAuthorizationManager.CreateEvaluatorAsync(
            instance,
            workflow,
            requestContext,
            pathRoleGrants.SelectMany(kv => kv.Value),
            cancellationToken);

        var visiblePaths = SchemaFieldVisibilityService.GetVisiblePaths(pathRoleGrants, callerRoles, evaluator);
        var pathsWithRoles = new HashSet<string>(pathRoleGrants.Keys, StringComparer.Ordinal);
        return InstanceDataRoleFilter.FilterByVisiblePaths(element, pathsWithRoles, visiblePaths);
    }
}
