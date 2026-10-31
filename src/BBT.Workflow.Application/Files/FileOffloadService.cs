// FileOffloadService.cs
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Caching;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>
/// Swaps <c>x-storage</c> <c>content</c> for a <see cref="FileHandle"/> before anything is persisted or enqueued
/// (vnext-client-sdk-core#101). Rules: spec §3.
/// </summary>
public sealed class FileOffloadService(
    IComponentCacheStore componentCacheStore,
    IFileBlobStore blobStore,
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<FileStorageOptions> options,
    ILogger<FileOffloadService> logger,
    IFileStagingStore? staging = null) : IFileOffloadService
{
    /// <summary>A stored <c>name</c> is capped to this many characters (the extension is kept when it is short).</summary>
    internal const int MaxNameLength = 255;
    private const int MaxKeptExtensionLength = 16;

    // Content-addressed: the component cache hands out a fresh SchemaDefinition per call (L1 holds bytes), and the
    // generation token behind it is not exposed on the resolved schema, so the key is the schema's exact UTF-8 JSON.
    // Looked up through a ReadOnlySpan<byte> alternate key: a hit reads the element's raw bytes in place and
    // allocates nothing; only a miss copies them into a key. Never keyed by (domain, key, version): a republish of
    // the same version must not keep serving the old fields.
    private static readonly ConcurrentDictionary<byte[], IReadOnlyList<FileStorageField>> Memo = new(Utf8KeyComparer.Instance);
    private static readonly ConcurrentDictionary<byte[], IReadOnlyList<FileStorageField>>.AlternateLookup<ReadOnlySpan<byte>> MemoLookup =
        Memo.GetAlternateLookup<ReadOnlySpan<byte>>();

    private abstract record Plan(JsonObject Node, string Path);
    private sealed record StorePlan(JsonObject Node, string Path, FileStorageField Field, byte[] Bytes, string? Name, string? MimeType)
        : Plan(Node, Path);
    private sealed record ReplacePlan(JsonObject Node, string Path, FileHandle Match) : Plan(Node, Path);
    private sealed record StagedPlan(JsonObject Node, string Path, StagedFile Staged) : Plan(Node, Path);

    public async Task<Result<IReadOnlyList<FileStorageField>>> GetFieldsAsync(Definitions.Workflow workflow, CancellationToken cancellationToken)
    {
        if (workflow.Schema is null)
            return Result<IReadOnlyList<FileStorageField>>.Ok([]);
        var schema = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        if (!schema.IsSuccess)
        {
            logger.FileSchemaUnavailable(workflow.Schema.Key, workflow.Key, schema.Error.Message ?? schema.Error.Code);
            return Result<IReadOnlyList<FileStorageField>>.Fail(WorkflowErrors.FileSchemaUnavailable(workflow.Schema.Key));
        }
        return Result<IReadOnlyList<FileStorageField>>.Ok(GetFields(schema.Value!));
    }

    public IReadOnlyList<FileStorageField> GetFields(SchemaDefinition schema)
    {
        var element = schema.Schema;
        if (element.ValueKind is JsonValueKind.Undefined)
            return [];

        var raw = JsonMarshal.GetRawUtf8Value(element);
        if (MemoLookup.TryGetValue(raw, out var fields))
            return fields;

        fields = FileStorageSchemaParser.Parse(element);
        Memo.TryAdd(raw.ToArray(), fields);
        return fields;
    }

    public async Task<Result<FileOffloadResult>> OffloadAsync(FileOffloadRequest request, CancellationToken cancellationToken)
    {
        if (request.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return Unchanged(request);

        var fields = request.Fields;
        if (fields is null)
        {
            var resolved = FileStorageFields.ForPayload(await GetFieldsAsync(request.Workflow, cancellationToken), payload);
            if (!resolved.IsSuccess)
                return Result<FileOffloadResult>.Fail(resolved.Error);
            fields = resolved.Value!;
        }
        if (fields.Count == 0)
            return Unchanged(request);

        var allowed = options.Value.AllowedComponents(fields);

        var root = JsonObject.Create(payload)!;
        var stored = request.LatestData is { ValueKind: JsonValueKind.Object } latest ? JsonObject.Create(latest) : null;

        // Pass 1: validate everything and plan; a rejected request writes nothing.
        var plans = new List<Plan>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var (node, path) in FileNodeWalker.Find(root, field).ToList())
            {
                var (plan, rejection) = await PlanAsync(request, field, node, path, stored, allowed, claimed, cancellationToken);
                if (rejection is not null)
                    return Invalid(request, path, rejection);
                if (plan is not null)
                    plans.Add(plan);
            }
        }

        if (plans.Count == 0)
            return Unchanged(request);

        // A streamed upload is single-use: take every referenced ticket atomically before anything is written, so two
        // requests referencing the same file cannot both claim it. A request that fails after this point puts its
        // tickets back.
        var staged = plans.OfType<StagedPlan>().ToList();
        var taken = await TakeStagedAsync(staged, cancellationToken);
        if (taken is not null)
            return Invalid(request, taken.Path, "the referenced file is not stored at this path on this instance");

        // Pass 2: write blobs, then swap nodes.
        foreach (var plan in plans)
        {
            switch (plan)
            {
                case StorePlan sp:
                    var handle = await StoreAsync(request, sp, cancellationToken);
                    if (!handle.IsSuccess)
                    {
                        await RestoreStagedAsync(staged, cancellationToken);
                        return Result<FileOffloadResult>.Fail(handle.Error);
                    }
                    Replace(sp.Node, handle.Value!);
                    break;
                case ReplacePlan rp:
                    Replace(rp.Node, rp.Match.ToJsonNode().AsObject());
                    break;
                case StagedPlan stp:
                    var owner = new FileOwner(runtimeInfoProvider.Domain, request.Workflow.Key, request.InstanceId.ToString());
                    Replace(stp.Node, stp.Staged.ToHandle(owner).ToJsonNode().AsObject());
                    logger.FileUploadClaimed(request.InstanceId, stp.Path, stp.Staged.Component, stp.Staged.File);
                    break;
            }
        }

        return Result<FileOffloadResult>.Ok(new FileOffloadResult(JsonSerializer.SerializeToElement(root), true));
    }

    /// <summary>
    /// The plan for one file node, or the reason it is refused. A node with neither <c>content</c> nor <c>file</c>, and a
    /// valid handle in a Trusted payload, need no plan.
    /// </summary>
    private async Task<(Plan? Plan, string? Rejection)> PlanAsync(
        FileOffloadRequest request, FileStorageField field, JsonObject node, string path, JsonObject? stored,
        IReadOnlySet<string> allowed, HashSet<string> claimed, CancellationToken cancellationToken)
    {
        var hasContent = node.ContainsKey("content");
        var hasFile = node.ContainsKey("file");

        if (hasContent && hasFile)
            return (null, "'content' and 'file' cannot be sent together");

        if (hasContent)
            return PlanInline(request, field, node, path);

        if (!hasFile)
            return (null, null);

        if (request.Mode == FileOffloadMode.Trusted)
        {
            // Kept as is, but only a handle the runtime could have written: a GUID file id in a component
            // the flow declares or the deployment allows (FileStorage:AllowedBindings).
            return FileHandle.TryRead(node) is { } trusted && trusted.IsAllowed(allowed)
                ? (null, null)
                : (null, "the file reference is not a valid handle of an allowed component");
        }

        var fileId = ReadString(node, "file");
        var match = fileId is null
            ? null
            : FileNodeWalker.Find(stored, field)
                .Select(h => FileHandle.TryRead(h.Node))
                .FirstOrDefault(h => h is not null && string.Equals(h.File, fileId, StringComparison.Ordinal));
        if (match is null)
        {
            // Not an echo: it can only be a streamed upload staged for this flow and path, claimed once.
            var staged = await FindStagedAsync(request, field, fileId, cancellationToken);
            return staged is not null && claimed.Add(staged.File)
                ? (new StagedPlan(node, path, staged), null)
                : (null, "the referenced file is not stored at this path on this instance");
        }
        // The stored record is not trusted on its own either: an invalid stored handle is never echoed.
        return match.IsAllowed(allowed)
            ? (new ReplacePlan(node, path, match), null)
            : (null, "the stored file reference is not a valid handle of an allowed component");
    }

    private static (Plan? Plan, string? Rejection) PlanInline(
        FileOffloadRequest request, FileStorageField field, JsonObject node, string path)
    {
        // x-storage.transport "stream": a client sends the bytes to the upload endpoint, never inline.
        if (request.Mode == FileOffloadMode.External && !field.AcceptsInline)
            return (null, "the field accepts streamed uploads only (x-storage.transport is \"stream\"); upload the file and reference its 'file'");
        if (node["content"] is not JsonValue cv || !cv.TryGetValue<string>(out var b64))
            return (null, "'content' must be a base64 string");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { return (null, "'content' is not valid base64"); }
        return (new StorePlan(node, path, field, bytes,
            NormalizeName(ReadString(node, "name")), NormalizeMediaType(ReadString(node, "mimeType"))), null);
    }

    /// <summary>
    /// Takes every staged ticket of <paramref name="plans"/> out of the store; null when all were taken, else the first
    /// plan whose ticket was gone (another request claimed it meanwhile). The ones taken before it are put back.
    /// </summary>
    private async Task<StagedPlan?> TakeStagedAsync(List<StagedPlan> plans, CancellationToken cancellationToken)
    {
        for (var i = 0; i < plans.Count; i++)
        {
            if (await staging!.TakeAsync(plans[i].Staged.Domain, plans[i].Staged.File, cancellationToken) is not null)
                continue;
            await RestoreStagedAsync(plans.GetRange(0, i), cancellationToken);
            return plans[i];
        }
        return null;
    }

    private async Task RestoreStagedAsync(List<StagedPlan> plans, CancellationToken cancellationToken)
    {
        foreach (var plan in plans)
            await staging!.RestoreAsync(plan.Staged, cancellationToken);
    }

    /// <summary>
    /// The staged upload <paramref name="fileId"/> when the field takes streamed uploads and the upload was made for
    /// this flow, this path and the field's component; null otherwise (the reference is then refused).
    /// </summary>
    private async Task<StagedFile?> FindStagedAsync(
        FileOffloadRequest request, FileStorageField field, string? fileId, CancellationToken cancellationToken)
    {
        if (staging is null || !field.AcceptsStream || !FileHandle.IsFileId(fileId))
            return null;
        var staged = await staging.GetAsync(runtimeInfoProvider.Domain, fileId!, cancellationToken);
        return staged is not null
               && string.Equals(staged.Flow, request.Workflow.Key, StringComparison.Ordinal)
               && string.Equals(staged.Path, field.Path, StringComparison.Ordinal)
               && string.Equals(staged.Component, field.Binding, StringComparison.Ordinal)
            ? staged
            : null;
    }

    /// <summary>
    /// The client-declared media type, normalised (<c>type/subtype</c> plus parameters, as the header parser prints it),
    /// or null when it does not parse — the read side then serves <c>application/octet-stream</c>.
    /// </summary>
    public static string? NormalizeMediaType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)
            || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(mimeType.Trim(), out var parsed)
            || parsed.MediaType is not { } mediaType
            || mediaType.IndexOf('/') <= 0)
            return null;
        return parsed.ToString();
    }

    /// <summary>
    /// The client-declared file name with control characters replaced and capped at <see cref="MaxNameLength"/>
    /// characters; a short extension survives the cut (<c>very-long…name.pdf</c>).
    /// </summary>
    public static string? NormalizeName(string? name)
    {
        if (name is null)
            return null;
        if (name.Any(char.IsControl))
            name = new string(name.Select(c => char.IsControl(c) ? '_' : c).ToArray());
        if (name.Length <= MaxNameLength)
            return name;

        var extension = Path.GetExtension(name);
        if (extension.Length is > 1 and <= MaxKeptExtensionLength)
            return string.Concat(name.AsSpan(0, CutAt(name, MaxNameLength - extension.Length)), extension);
        return name[..CutAt(name, MaxNameLength)];
    }

    // Never split a surrogate pair: the handle is serialized as JSON.
    private static int CutAt(string value, int length)
        => length > 0 && char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;

    private static string? ReadString(JsonObject node, string name)
        => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private async Task<Result<JsonObject>> StoreAsync(FileOffloadRequest request, StorePlan plan, CancellationToken cancellationToken)
    {
        var file = Guid.NewGuid().ToString();
        var put = await blobStore.PutAsync(plan.Field.Binding, file, plan.Bytes, plan.MimeType, cancellationToken);
        if (!put.IsSuccess)
            return Result<JsonObject>.Fail(put.Error);

        var handle = new FileHandle(plan.Field.Binding, file, plan.Name, plan.MimeType, plan.Bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(plan.Bytes)),
            new FileOwner(runtimeInfoProvider.Domain, request.Workflow.Key, request.InstanceId.ToString()));
        logger.FileOffloaded(request.InstanceId, plan.Path, plan.Field.Binding, file, plan.Bytes.LongLength);
        return Result<JsonObject>.Ok(handle.ToJsonNode().AsObject());
    }

    /// <summary>
    /// Swaps only the file members of <paramref name="target"/>: <c>content</c> is dropped and every handle member is
    /// written from <paramref name="source"/> (the handle wins its own members, e.g. a client-sent <c>size</c>). Any
    /// other member the client put on the node stays — a FilePicker row's <c>documentType</c> must reach the mapping
    /// (vnext-client-sdk-core#137).
    /// </summary>
    private static void Replace(JsonObject target, JsonObject source)
    {
        target.Remove("content");
        foreach (var (key, value) in source.ToList())
        {
            source.Remove(key);
            target[key] = value;
        }
    }

    private Result<FileOffloadResult> Invalid(FileOffloadRequest request, string path, string reason)
    {
        logger.FileReferenceRejected(request.InstanceId, path, reason);
        return Result<FileOffloadResult>.Fail(WorkflowErrors.FileReferenceInvalid(path, reason));
    }

    private static Result<FileOffloadResult> Unchanged(FileOffloadRequest request)
        => Result<FileOffloadResult>.Ok(new FileOffloadResult(request.Payload, false));
}

/// <summary>Byte-wise key equality with a <see cref="ReadOnlySpan{T}"/> alternate, so memo hits allocate nothing.</summary>
internal sealed class Utf8KeyComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static readonly Utf8KeyComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj) => GetHashCode((ReadOnlySpan<byte>)obj);

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        var hash = new HashCode();
        hash.AddBytes(alternate);
        return hash.ToHashCode();
    }

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
}
