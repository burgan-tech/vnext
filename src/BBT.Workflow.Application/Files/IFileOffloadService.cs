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
    /// <see cref="GetFields"/> directly.
    /// </summary>
    Task<IReadOnlyList<FileStorageField>> GetFieldsAsync(Definitions.Workflow workflow, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>x-storage</c> fields of an already-resolved master schema. Memoized by the schema's content (exact UTF-8
    /// bytes, compared without allocating), so a republished schema is re-parsed and an unchanged one is not.
    /// </summary>
    IReadOnlyList<FileStorageField> GetFields(SchemaDefinition schema);
}
