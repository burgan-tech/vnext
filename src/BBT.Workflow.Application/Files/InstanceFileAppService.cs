using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>
/// Reads an x-storage file of ONE instance: the handle must be in that instance's latest data at one of its
/// schema's x-storage paths (no subflow descent, no history), so the binding and object key always come from the
/// record, never from the caller — and the record itself must name a GUID file in an allowed component. With
/// <see cref="InstanceFileRequest.Authorization"/> it applies the state's queryRoles before locating the file (denied →
/// 403, whether or not the file exists) and the x-roles of the file's path and every ancestor (hidden → 404, existence
/// is not revealed); without it, it is the service-to-service read (internal endpoint, scripts).
/// </summary>
/// <remarks>Runs inside the instance flow's schema scope: the HTTP route establishes it, the local gateway opens it.</remarks>
public sealed class InstanceFileAppService(
    IInstanceRepository instanceRepository,
    IComponentCacheStore componentCacheStore,
    IFileOffloadService offloadService,
    IFileBlobStore blobStore,
    ICallerRoleResolver callerRoleResolver,
    ITransitionAuthorizationManager authorizationManager,
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<FileStorageOptions> options,
    ILogger<InstanceFileAppService> logger) : IInstanceFileAppService
{
    private const string ArraySegment = "[]";

    /// <inheritdoc />
    public async Task<Result<InstanceFileContent>> ReadAsync(InstanceFileRequest request, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(request, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<InstanceFileContent>.Fail(resolved.Error);
        var (instanceId, handle, path) = resolved.Value;

        if (MatchesIfNoneMatch(request.IfNoneMatch, handle.ETag))
            return Result<InstanceFileContent>.Ok(new InstanceFileContent(handle, path, null, NotModified: true));

        var bytes = await ReadVerifiedAsync(instanceId, handle, cancellationToken);
        return bytes.IsSuccess
            ? Result<InstanceFileContent>.Ok(new InstanceFileContent(handle, path, bytes.Value, NotModified: false))
            : Result<InstanceFileContent>.Fail(bytes.Error);
    }

    /// <inheritdoc />
    public async Task<Result<InstanceFileStream>> OpenAsync(InstanceFileRequest request, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(request, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<InstanceFileStream>.Fail(resolved.Error);
        var (instanceId, handle, path) = resolved.Value;

        if (MatchesIfNoneMatch(request.IfNoneMatch, handle.ETag))
            return Result<InstanceFileStream>.Ok(new InstanceFileStream(handle, path, null, NotModified: true));

        var opened = await blobStore.OpenReadAsync(handle.Component, handle.File, cancellationToken);
        if (!opened.IsSuccess)
            return Result<InstanceFileStream>.Fail(opened.Error);
        var blob = opened.Value!;

        // A streaming store serves the object as is once its length is the handle's (the s3 provider checked the
        // stored size when it wrote it). Anything else is read whole and verified, which also recovers an object a
        // Dapr 1.17+ S3 binding stored aws-chunked framed.
        if (!blob.IsBuffered && blob.Length == handle.Size)
            return Result<InstanceFileStream>.Ok(new InstanceFileStream(handle, path, blob.Content, NotModified: false));

        byte[] stored;
        await using (blob.Content)
        {
            if (blob.Content is MemoryStream buffered)
                stored = buffered.ToArray();
            else
            {
                var copy = new MemoryStream();
                await blob.Content.CopyToAsync(copy, cancellationToken);
                stored = copy.ToArray();
            }
        }

        var verified = Verify(instanceId, handle, stored);
        return verified.IsSuccess
            ? Result<InstanceFileStream>.Ok(new InstanceFileStream(
                handle, path, new MemoryStream(verified.Value!, writable: false), NotModified: false))
            : Result<InstanceFileStream>.Fail(verified.Error);
    }

    /// <summary>
    /// The lookup and authorization both reads share: the handle at one of the flow's x-storage paths in the latest
    /// data, after the state's queryRoles (403) and the path's x-roles (404) when the caller is public.
    /// </summary>
    private async Task<Result<(Guid InstanceId, FileHandle Handle, string Path)>> ResolveAsync(
        InstanceFileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Same treatment as the data/state functions: a route domain that is not this runtime's is 404 (NotFoundDomain).
        runtimeInfoProvider.Check(request.Domain);

        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(request.Instance, cancellationToken);
        // A different flow sharing the schema must not serve this route's files.
        if (instance is null || !string.Equals(instance.Flow, request.Flow, StringComparison.Ordinal))
            return Result<(Guid, FileHandle, string)>.Fail(WorkflowErrors.FileNotFound(request.File));

        var flow = await componentCacheStore.GetFlowAsync(request.Domain, instance.Flow, instance.FlowVersion, cancellationToken);
        if (!flow.IsSuccess)
            return Result<(Guid, FileHandle, string)>.Fail(flow.Error);
        var workflow = flow.Value!;

        // The state's queryRoles are decided BEFORE the file is located: a denied caller gets 403 whether or not the
        // file exists, so the answer is no existence oracle. x-roles need the file's path and come after (hidden ⇒ 404).
        IReadOnlyList<string>? callerRoles = null;
        if (request.Authorization is { } authorization)
        {
            var allowed = await AuthorizeQueryAsync(workflow, instance, authorization, cancellationToken);
            if (!allowed.IsSuccess)
                return Result<(Guid, FileHandle, string)>.Fail(allowed.Error);
            callerRoles = allowed.Value!;
        }

        var located = await LocateAsync(workflow, instance, request.File, cancellationToken);
        if (!located.IsSuccess)
            return Result<(Guid, FileHandle, string)>.Fail(located.Error);
        if (located.Value is not { } found)
            return Result<(Guid, FileHandle, string)>.Fail(WorkflowErrors.FileNotFound(request.File));
        var (handle, path, field) = found;

        if (request.Authorization is { } pathAuthorization
            && !await IsPathVisibleAsync(workflow, instance, field, callerRoles!, pathAuthorization, cancellationToken))
            return Result<(Guid, FileHandle, string)>.Fail(WorkflowErrors.FileNotFound(request.File));

        logger.FileRead(request.Domain, instance.Flow, instance.Id.ToString(), handle.File, request.Authorization is not null);
        return Result<(Guid, FileHandle, string)>.Ok((instance.Id, handle, path));
    }

    private async Task<Result<byte[]>> ReadVerifiedAsync(Guid instanceId, FileHandle handle, CancellationToken cancellationToken)
    {
        var bytes = await blobStore.GetAsync(handle.Component, handle.File, cancellationToken);
        return bytes.IsSuccess ? Verify(instanceId, handle, bytes.Value!) : Result<byte[]>.Fail(bytes.Error);
    }

    /// <summary>
    /// The stored bytes are never trusted to be the file: they must match the handle's <c>size</c> and <c>eTag</c>
    /// (vnext-client-sdk-core#145). An aws-chunked framed object whose payload matches is recovered; anything else is
    /// <c>FileStoreUnavailable</c> (503) with the reason in the log, never a silently corrupt body.
    /// </summary>
    private Result<byte[]> Verify(Guid instanceId, FileHandle handle, byte[] stored)
    {
        var check = StoredFileVerifier.Check(handle, stored);
        switch (check.State)
        {
            case StoredFileState.Intact:
                return Result<byte[]>.Ok(check.Content!);
            case StoredFileState.RecoveredFromChunkedFrame:
                logger.FileRecoveredFromChunkedFrame(instanceId, handle.Component, handle.File, stored.LongLength, handle.Size);
                return Result<byte[]>.Ok(check.Content!);
            default:
                logger.FileIntegrityMismatch(instanceId, handle.Component, handle.File, stored.LongLength, handle.Size, handle.ETag);
                return Result<byte[]>.Fail(WorkflowErrors.FileStoreUnavailable(handle.Component));
        }
    }

    /// <summary>
    /// The handle at one of the flow's x-storage paths in the latest data whose <c>file</c> is <paramref name="file"/>;
    /// null when there is none. The stored record alone is not trusted: a handle whose <c>file</c> is not a GUID or whose
    /// <c>component</c> is neither declared by the flow's master schema nor in <c>FileStorage:AllowedBindings</c> is
    /// treated as absent (404) and the store is never called with it. A master schema that cannot be loaded is
    /// <c>FileSchemaUnavailable</c> (503).
    /// </summary>
    private async Task<Result<(FileHandle Handle, string Path, FileStorageField Field)?>> LocateAsync(
        Definitions.Workflow workflow, Instance instance, string file, CancellationToken cancellationToken)
    {
        var latest = instance.LatestData;
        if (latest is null || !FileHandle.IsFileId(file))
            return Result<(FileHandle, string, FileStorageField)?>.Ok(null);

        var resolved = await offloadService.GetFieldsAsync(workflow, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<(FileHandle, string, FileStorageField)?>.Fail(resolved.Error);
        var fields = resolved.Value!;
        if (fields.Count == 0)
            return Result<(FileHandle, string, FileStorageField)?>.Ok(null);

        var allowed = options.Value.AllowedComponents(fields);
        var root = JsonNode.Parse(latest.Data.Json);
        foreach (var field in fields)
        {
            foreach (var (node, path) in FileNodeWalker.Find(root, field))
            {
                if (FileHandle.TryRead(node) is not { } handle || !string.Equals(handle.File, file, StringComparison.Ordinal))
                    continue;
                if (!handle.IsAllowed(allowed))
                {
                    logger.FileHandleRejectedOnRead(instance.Id, path, handle.Component);
                    return Result<(FileHandle, string, FileStorageField)?>.Ok(null);
                }
                return Result<(FileHandle, string, FileStorageField)?>.Ok((handle, path, field));
            }
        }

        return Result<(FileHandle, string, FileStorageField)?>.Ok(null);
    }

    /// <summary>The caller's roles when the state's queryRoles allow the read; the denial otherwise.</summary>
    private async Task<Result<IReadOnlyList<string>>> AuthorizeQueryAsync(
        Definitions.Workflow workflow,
        Instance instance,
        AuthorizationRequestContext authorization,
        CancellationToken cancellationToken)
    {
        // A provider failure is a denial (ICallerRoleResolver contract); neither built-in provider fails.
        var resolved = await callerRoleResolver.ResolveRolesAsync(authorization.Headers, cancellationToken);
        if (!resolved.IsSuccess)
            return Result<IReadOnlyList<string>>.Fail(resolved.Error);
        IReadOnlyList<string> callerRoles = resolved.Value ?? [];

        return await authorizationManager.IsQueryAllowedAsync(workflow, instance, callerRoles, authorization, cancellationToken)
            ? Result<IReadOnlyList<string>>.Ok(callerRoles)
            : Result<IReadOnlyList<string>>.Fail(WorkflowErrors.QueryAccessDenied(instance.EffectiveState ?? instance.GetCurrentState));
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
