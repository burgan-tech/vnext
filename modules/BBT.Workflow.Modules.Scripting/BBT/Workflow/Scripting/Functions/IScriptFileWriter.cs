using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>The instance a script-written file belongs to (the handle's <c>owner</c>).</summary>
public sealed record ScriptFileOwner(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("flow")] string Flow,
    [property: JsonPropertyName("instance")] string Instance);

/// <summary>
/// The handle of a file a script stored with <c>ScriptBase.PutFileAsync</c>, in the persisted x-storage shape. Put it
/// at an x-storage path of the mapping's output (or a task's output) and the runtime keeps it as the field's handle.
/// </summary>
public sealed record ScriptStoredFile(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("eTag")] string ETag,
    [property: JsonPropertyName("owner")] ScriptFileOwner Owner);

/// <summary>
/// Stores a file a script produced (a converted, merged or compressed document) into an x-storage component, and
/// names the object key a binding task needs to act on a stored file. A failure throws, like any helper failure.
/// </summary>
public interface IScriptFileWriter
{
    /// <summary>
    /// Writes <paramref name="length"/> bytes of <paramref name="content"/> to <paramref name="component"/> for the
    /// instance <paramref name="instance"/> of <paramref name="domain"/>/<paramref name="flow"/>. The component must be
    /// an x-storage binding of that flow's master schema or in <c>FileStorage:AllowedBindings</c>.
    /// </summary>
    Task<ScriptStoredFile> WriteAsync(
        string domain, string flow, string instance, string component, Stream content, long length,
        string? name, string? mimeType, CancellationToken cancellationToken);

    /// <summary>The object key of <paramref name="file"/> in its store (<c>FileStorage:KeyPrefix</c> + file id).</summary>
    string ObjectKey(string file);
}
