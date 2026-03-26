using System.Text.Json;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Applies master schema field-level visibility filtering to instance data based on effective caller roles.
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

        var callerRoles = instance != null
            ? await transitionAuthorizationManager.GetEffectiveCallerRolesForFieldVisibilityAsync(instance,
                cancellationToken)
            : currentUser.Roles;
        var visiblePaths = SchemaFieldVisibilityService.GetVisiblePaths(pathRoleGrants, callerRoles);
        var pathsWithRoles = new HashSet<string>(pathRoleGrants.Keys, StringComparer.Ordinal);
        return InstanceDataRoleFilter.FilterByVisiblePaths(element, pathsWithRoles, visiblePaths);
    }
}
