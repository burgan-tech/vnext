using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Files;

/// <summary>
/// Reads an x-storage file of ONE instance: the handle must be in that instance's latest data at one of its
/// schema's x-storage paths (no subflow descent, no history), so the binding and object key always come from the
/// record, never from the caller. With <see cref="InstanceFileRequest.Authorization"/> it applies the state's
/// queryRoles (denied → 403) and the x-roles of the file's path and every ancestor (hidden → 404, existence is not
/// revealed); without it, it is the service-to-service read (internal endpoint, scripts).
/// </summary>
/// <remarks>Runs inside the instance flow's schema scope: the HTTP route establishes it, the local gateway opens it.</remarks>
public sealed class InstanceFileAppService(
    IInstanceRepository instanceRepository,
    IComponentCacheStore componentCacheStore,
    IFileOffloadService offloadService,
    IFileBlobStore blobStore,
    ICallerRoleResolver callerRoleResolver,
    ITransitionAuthorizationManager authorizationManager,
    ILogger<InstanceFileAppService> logger) : IInstanceFileAppService
{
    private const string ArraySegment = "[]";

    /// <inheritdoc />
    public async Task<Result<InstanceFileContent>> ReadAsync(InstanceFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(request.Instance, cancellationToken);
        // A different flow sharing the schema must not serve this route's files.
        if (instance is null || !string.Equals(instance.Flow, request.Flow, StringComparison.Ordinal))
            return Result<InstanceFileContent>.Fail(WorkflowErrors.FileNotFound(request.File));

        var flow = await componentCacheStore.GetFlowAsync(request.Domain, instance.Flow, instance.FlowVersion, cancellationToken);
        if (!flow.IsSuccess)
            return Result<InstanceFileContent>.Fail(flow.Error);
        var workflow = flow.Value!;

        var located = await LocateAsync(workflow, instance, request.File, cancellationToken);
        if (located is null)
            return Result<InstanceFileContent>.Fail(WorkflowErrors.FileNotFound(request.File));
        var (handle, path, field) = located.Value;

        if (request.Authorization is { } authorization)
        {
            var denied = await AuthorizeAsync(workflow, instance, field, request.File, authorization, cancellationToken);
            if (denied is { } error)
                return Result<InstanceFileContent>.Fail(error);
        }

        logger.FileRead(request.Domain, instance.Flow, instance.Id.ToString(), handle.File, request.Authorization is not null);

        if (MatchesIfNoneMatch(request.IfNoneMatch, handle.ETag))
            return Result<InstanceFileContent>.Ok(new InstanceFileContent(handle, path, null, NotModified: true));

        var bytes = await blobStore.GetAsync(handle.Component, handle.File, cancellationToken);
        return bytes.IsSuccess
            ? Result<InstanceFileContent>.Ok(new InstanceFileContent(handle, path, bytes.Value, NotModified: false))
            : Result<InstanceFileContent>.Fail(bytes.Error);
    }

    private async Task<(FileHandle Handle, string Path, FileStorageField Field)?> LocateAsync(
        Definitions.Workflow workflow, Instance instance, string file, CancellationToken cancellationToken)
    {
        var latest = instance.LatestData;
        if (latest is null)
            return null;

        var fields = await offloadService.GetFieldsAsync(workflow, cancellationToken);
        if (fields.Count == 0)
            return null;

        var root = JsonNode.Parse(latest.Data.Json);
        foreach (var field in fields)
        {
            foreach (var (node, path) in FileNodeWalker.Find(root, field))
            {
                if (FileHandle.TryRead(node) is { } handle && string.Equals(handle.File, file, StringComparison.Ordinal))
                    return (handle, path, field);
            }
        }

        return null;
    }

    /// <summary>Returns the denial, or <c>null</c> when the caller may read the file.</summary>
    private async Task<Error?> AuthorizeAsync(
        Definitions.Workflow workflow,
        Instance instance,
        FileStorageField field,
        string file,
        AuthorizationRequestContext authorization,
        CancellationToken cancellationToken)
    {
        // A provider failure is a denial (ICallerRoleResolver contract); neither built-in provider fails.
        var resolved = await callerRoleResolver.ResolveRolesAsync(authorization.Headers, cancellationToken);
        if (!resolved.IsSuccess)
            return resolved.Error;
        IReadOnlyList<string> callerRoles = resolved.Value ?? [];

        if (!await authorizationManager.IsQueryAllowedAsync(workflow, instance, callerRoles, authorization, cancellationToken))
            return WorkflowErrors.QueryAccessDenied(instance.EffectiveState ?? instance.GetCurrentState);

        return await IsPathVisibleAsync(workflow, instance, field, callerRoles, authorization, cancellationToken)
            ? null
            : WorkflowErrors.FileNotFound(file);
    }

    /// <summary>
    /// x-roles on the file's path and every ancestor: each guarded prefix must allow the caller — the same per-path
    /// rule the data filter prunes with, applied to the chain the data filter would prune top-down. Array markers are
    /// not path segments (<c>files[]</c> is guarded by the grant on <c>files</c>). A schema that cannot be read hides
    /// the file (fail closed).
    /// </summary>
    private async Task<bool> IsPathVisibleAsync(
        Definitions.Workflow workflow,
        Instance instance,
        FileStorageField field,
        IReadOnlyList<string> callerRoles,
        AuthorizationRequestContext authorization,
        CancellationToken cancellationToken)
    {
        if (workflow.Schema is null)
            return true;

        var schema = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        if (!schema.IsSuccess)
            return false;

        var grants = SchemaRolesParser.ParseExposure(schema.Value!.Schema).PathRoleGrants;
        if (grants.Count == 0)
            return true;

        var guarded = new List<IReadOnlyList<RoleGrant>>();
        var prefix = string.Empty;
        foreach (var segment in field.Segments)
        {
            if (segment == ArraySegment)
                continue;
            prefix = prefix.Length == 0 ? segment : $"{prefix}.{segment}";
            if (grants.TryGetValue(prefix, out var pathGrants) && pathGrants.Count > 0)
                guarded.Add(pathGrants);
        }

        if (guarded.Count == 0)
            return true;

        var evaluator = await authorizationManager.CreateEvaluatorAsync(
            instance, workflow, authorization, guarded.SelectMany(g => g).ToList(), cancellationToken);
        return guarded.All(g => SchemaFieldVisibilityService.IsPathVisibleForCaller(g, callerRoles, evaluator));
    }

    /// <summary>
    /// <c>If-None-Match</c> weak comparison (RFC 9110 §13.1.2): <c>*</c>, or any listed entity tag whose opaque value
    /// equals the handle's quoted <c>eTag</c> (a <c>W/</c> prefix is ignored). An unquoted value never matches.
    /// </summary>
    internal static bool MatchesIfNoneMatch(string? ifNoneMatch, string eTag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
            return false;

        var quoted = $"\"{eTag}\"";
        foreach (var raw in ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*")
                return true;
            var tag = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (string.Equals(tag, quoted, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
