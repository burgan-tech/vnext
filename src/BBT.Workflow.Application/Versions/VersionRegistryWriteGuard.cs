using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Versions;

/// <summary>
/// Default <see cref="IVersionRegistryWriteGuard"/>: a pointer is writable when it is automatic (null or "latest"), or an accepted
/// form that resolves to a published version of the target flow. Published versions are read straight from
/// the backend, never through <c>CacheSet</c> (that would apply the very policy being written).
/// </summary>
/// <remarks>The informational <c>type</c> member is never read.</remarks>
public sealed class VersionRegistryWriteGuard(
    ICacheBackend<Definitions.Workflow> backend,
    ILogger<VersionRegistryWriteGuard> logger) : IVersionRegistryWriteGuard
{
    private static readonly JsonSerializerOptions EntryOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<Result> ValidateAsync(
        string domain,
        string flowKey,
        JsonElement? data,
        CancellationToken cancellationToken = default)
    {
        if (data is not { ValueKind: JsonValueKind.Object } payload)
            return Result.Ok();

        string? pointer;
        try
        {
            pointer = payload.Deserialize<VersionRegistryEntry>(EntryOptions)?.DefaultVersion;
        }
        catch (JsonException)
        {
            // defaultVersion present but not a string (number, object, ...): not an accepted form.
            return Reject(domain, flowKey, ReadRawPointer(payload));
        }

        // null, or "latest" (Automatic, the way to clear): nothing to resolve against published versions.
        if (DefaultVersionResolver.IsAutomaticPointer(pointer))
            return Result.Ok();

        if (!DefaultVersionResolver.IsValidPointer(pointer))
            return Reject(domain, flowKey, pointer);

        if (await LoadPublishedVersionsAsync(domain, flowKey, cancellationToken) is not { } published)
            return Result.Fail(WorkflowErrors.VersionRegistryUnavailable(domain, flowKey));

        if (!published.IsSuccess)
        {
            // "No such flow" is the caller's typo; anything else is a read failure to surface as is.
            return published.Error.Code == WorkflowErrorCodes.CacheItemNotFound
                ? Reject(domain, flowKey, pointer)
                : Result.Fail(published.Error);
        }

        var versions = published.Value!;
        var resolution = DefaultVersionResolver.Resolve(versions, pointer, requested: null);
        return versions.Count == 0 || resolution.Outcome == VersionResolutionOutcome.PointerInvalid
            ? Reject(domain, flowKey, pointer)
            : Result.Ok();
    }

    /// <summary>
    /// The flow's published version strings — validating a pointer never needs a body. Null when the read
    /// failed for a reason other than the caller's cancellation; the cause is logged and the client gets the
    /// fixed 503 text.
    /// </summary>
    private async Task<Result<IReadOnlyList<string>>?> LoadPublishedVersionsAsync(
        string domain,
        string flowKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await backend.LoadVersionsAsync(domain, flowKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.VersionRegistryReadFailed(domain, flowKey, WorkflowErrorCodes.VersionRegistryUnavailable, ex);
            return null;
        }
    }

    private Result Reject(string domain, string flowKey, string pointer)
    {
        logger.FlowDefaultVersionInvalid(domain, flowKey, pointer);
        return Result.Fail(WorkflowErrors.FlowDefaultVersionInvalid(domain, flowKey, pointer));
    }

    private static string ReadRawPointer(JsonElement payload) =>
        payload.TryGetProperty("defaultVersion", out var raw) ? raw.GetRawText() : string.Empty;
}
