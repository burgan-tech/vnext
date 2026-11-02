namespace BBT.Workflow.Files;

public sealed class FileStorageOptions
{
    public const string Section = "FileStorage";

    /// <summary>Prepended to every object key (e.g. <c>vnext-runtime/</c> in a shared bucket). Never change it after the first write.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Binding components a stored or runtime-produced handle may name besides the <c>x-storage</c> bindings declared in
    /// the flow's own master schema — for a handle that crosses flows (a SubFlow input, a mapping copying a parent's
    /// file). Empty by default. Helm deployments list their <c>blobStorageComponents</c> names here. A handle naming any
    /// other component is never read (404) and never accepted on write (400).
    /// </summary>
    public List<string> AllowedBindings { get; set; } = [];

    /// <summary>Streamed uploads (<c>POST …/workflows/{flow}/files</c>, <c>x-storage.transport</c> <c>stream</c>/<c>any</c>).</summary>
    public FileUploadOptions Upload { get; set; } = new();

    /// <summary>The components a handle may name for a flow whose declared x-storage fields are <paramref name="fields"/>.</summary>
    public IReadOnlySet<string> AllowedComponents(IReadOnlyList<Definitions.Schemas.FileStorageField> fields)
    {
        var allowed = new HashSet<string>(AllowedBindings.Where(b => !string.IsNullOrWhiteSpace(b)), StringComparer.Ordinal);
        foreach (var field in fields)
            allowed.Add(field.Binding);
        return allowed;
    }
}

/// <summary>Limits of the streamed upload endpoint.</summary>
public sealed class FileUploadOptions
{
    /// <summary>
    /// Largest accepted upload in bytes (default 64 MiB, the orchestration sidecar's shipped <c>--max-body-size</c>: the
    /// file reaches the store as one binding message). Larger requests answer 413 before any byte is stored. Raise it
    /// together with the sidecar's limit.
    /// </summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Minutes an uploaded file may wait to be referenced by a start or transition (default 60).</summary>
    public int TicketTtlMinutes { get; set; } = 60;
}
