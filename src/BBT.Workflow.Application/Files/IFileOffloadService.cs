// IFileOffloadService.cs
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Files;

public enum FileOffloadMode
{
    /// <summary>A client body: a content-less reference must already be stored at the same path on this instance.</summary>
    External,
    /// <summary>Runtime-produced data (mappings, task outputs, subflow input): references are kept as is.</summary>
    Trusted,
}

/// <param name="Fields">
/// The flow's <c>x-storage</c> fields when the caller already derived them (<see cref="IFileOffloadService.GetFields"/>);
/// null ⇒ the service derives them from <paramref name="Workflow"/>.
/// </param>
public sealed record FileOffloadRequest(
    Definitions.Workflow Workflow,
    Guid InstanceId,
    JsonElement? Payload,
    JsonElement? LatestData,
    FileOffloadMode Mode,
    IReadOnlyList<FileStorageField>? Fields = null);

public sealed record FileOffloadResult(JsonElement? Payload, bool Changed);

public interface IFileOffloadService
{
    Task<Result<FileOffloadResult>> OffloadAsync(FileOffloadRequest request, CancellationToken cancellationToken);
    /// <summary>
    /// The workflow's <c>x-storage</c> fields: loads the master schema through the component cache (which deserializes
    /// it per call), then <see cref="GetFields"/>. A caller that already holds the resolved schema uses
    /// <see cref="GetFields"/> directly. No master schema ⇒ no fields. A master schema that cannot be loaded ⇒
    /// <c>FileSchemaUnavailable</c> (503): the caller decides with <see cref="FileStorageFields.ForPayload"/> whether
    /// its payload is affected.
    /// </summary>
    Task<Result<IReadOnlyList<FileStorageField>>> GetFieldsAsync(Definitions.Workflow workflow, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>x-storage</c> fields of an already-resolved master schema. Memoized by the schema's content (exact UTF-8
    /// bytes, compared without allocating), so a republished schema is re-parsed and an unchanged one is not.
    /// </summary>
    IReadOnlyList<FileStorageField> GetFields(SchemaDefinition schema);
}

/// <summary>The fail-closed rule for a master schema that cannot be loaded (spec §3, I3).</summary>
public static class FileStorageFields
{
    /// <summary>
    /// <paramref name="fields"/> when they were resolved. When the master schema could not be loaded, the payload is
    /// refused (<c>FileSchemaUnavailable</c>) only if some object in it carries a <c>content</c> or <c>file</c> member —
    /// the paths are unknown, so bytes could be persisted inline or an unchecked reference kept. A payload with
    /// neither member is unaffected by any x-storage declaration and proceeds with no fields (the outage does not
    /// block flows or requests that carry no file).
    /// </summary>
    public static Result<IReadOnlyList<FileStorageField>> ForPayload(
        Result<IReadOnlyList<FileStorageField>> fields, JsonElement payload)
    {
        if (fields.IsSuccess)
            return fields;
        return FileNodeWalker.AnyFileShapedNode(payload)
            ? fields
            : Result<IReadOnlyList<FileStorageField>>.Ok([]);
    }
}
