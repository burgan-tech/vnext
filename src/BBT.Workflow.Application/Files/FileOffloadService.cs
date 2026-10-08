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

namespace BBT.Workflow.Files;

/// <summary>
/// Swaps <c>x-storage</c> <c>content</c> for a <see cref="FileHandle"/> before anything is persisted or enqueued
/// (vnext-client-sdk-core#101). Rules: spec §3.
/// </summary>
public sealed class FileOffloadService(
    IComponentCacheStore componentCacheStore,
    IFileBlobStore blobStore,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<FileOffloadService> logger) : IFileOffloadService
{
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

    public async Task<IReadOnlyList<FileStorageField>> GetFieldsAsync(Definitions.Workflow workflow, CancellationToken cancellationToken)
    {
        if (workflow.Schema is null)
            return [];
        var schema = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        if (!schema.IsSuccess)
            return [];
        return GetFields(schema.Value!);
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

        var fields = request.Fields ?? await GetFieldsAsync(request.Workflow, cancellationToken);
        if (fields.Count == 0)
            return Unchanged(request);

        var root = JsonObject.Create(payload)!;
        var stored = request.LatestData is { ValueKind: JsonValueKind.Object } latest ? JsonObject.Create(latest) : null;

        // Pass 1: validate everything and plan; a rejected request writes nothing.
        var plans = new List<Plan>();
        foreach (var field in fields)
        {
            foreach (var (node, path) in FileNodeWalker.Find(root, field).ToList())
            {
                var hasContent = node.ContainsKey("content");
                var hasFile = node.ContainsKey("file");

                if (hasContent && hasFile)
                    return Invalid(request, path, "'content' and 'file' cannot be sent together");

                if (hasContent)
                {
                    if (node["content"] is not JsonValue cv || !cv.TryGetValue<string>(out var b64))
                        return Invalid(request, path, "'content' must be a base64 string");
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(b64); }
                    catch (FormatException) { return Invalid(request, path, "'content' is not valid base64"); }
                    plans.Add(new StorePlan(node, path, field, bytes, ReadString(node, "name"), ReadString(node, "mimeType")));
                    continue;
                }

                if (!hasFile || request.Mode == FileOffloadMode.Trusted)
                    continue;

                var fileId = ReadString(node, "file");
                var match = fileId is null
                    ? null
                    : FileNodeWalker.Find(stored, field)
                        .Select(h => FileHandle.TryRead(h.Node))
                        .FirstOrDefault(h => h is not null && string.Equals(h.File, fileId, StringComparison.Ordinal));
                if (match is null)
                    return Invalid(request, path, "the referenced file is not stored at this path on this instance");
                plans.Add(new ReplacePlan(node, path, match));
            }
        }

        if (plans.Count == 0)
            return Unchanged(request);

        // Pass 2: write blobs, then swap nodes.
        foreach (var plan in plans)
        {
            switch (plan)
            {
                case StorePlan sp:
                    var handle = await StoreAsync(request, sp, cancellationToken);
                    if (!handle.IsSuccess)
                        return Result<FileOffloadResult>.Fail(handle.Error);
                    Replace(sp.Node, handle.Value!);
                    break;
                case ReplacePlan rp:
                    Replace(rp.Node, rp.Match.ToJsonNode().AsObject());
                    break;
            }
        }

        return Result<FileOffloadResult>.Ok(new FileOffloadResult(JsonSerializer.SerializeToElement(root), true));
    }

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

    private static void Replace(JsonObject target, JsonObject source)
    {
        target.Clear();
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
