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

    /// <summary>
    /// How each <c>x-storage</c> component is reached, keyed by the component name the master schema declares
    /// (<c>x-storage.binding</c>). A component with no entry, or with <c>Provider: dapr</c>, is a Dapr output binding of
    /// that name on the orchestration sidecar (the behaviour before this setting existed). <c>Provider: s3</c> talks to
    /// an S3-compatible store directly: uploads stream, reads fetch ranges, and the stored bytes are exactly the file
    /// (no aws-chunked framing, vnext-client-sdk-core#145).
    /// </summary>
    public Dictionary<string, FileStoreOptions> Stores { get; set; } = new(StringComparer.Ordinal);

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

    /// <summary>The store settings of <paramref name="component"/>; a component with no entry is a Dapr binding.</summary>
    public FileStoreOptions StoreOf(string component)
        => Stores.TryGetValue(component, out var store) ? store : FileStoreOptions.DaprBinding;
}

/// <summary>One <c>x-storage</c> component's provider settings (<c>FileStorage:Stores:{component}</c>).</summary>
public sealed class FileStoreOptions
{
    public const string DaprProvider = "dapr";
    public const string S3Provider = "s3";

    internal static readonly FileStoreOptions DaprBinding = new();

    /// <summary><c>dapr</c> (default): the Dapr output binding of the same name. <c>s3</c>: the S3 API directly.</summary>
    public string Provider { get; set; } = DaprProvider;

    /// <summary>S3 endpoint, e.g. <c>https://s3.example.com</c> or <c>http://minio:9000</c>. Empty: AWS for <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Signing region. S3-compatible stores usually accept the default.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Bucket that holds the objects. Required for <c>s3</c>.</summary>
    public string? Bucket { get; set; }

    /// <summary>Access key; empty uses the SDK's default credential chain (environment, web identity, instance role).</summary>
    public string? AccessKey { get; set; }

    /// <summary>Secret key paired with <see cref="AccessKey"/>. Supply it from a secret, never from a committed file.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Path-style addressing (<c>host/bucket/key</c>), which most S3-compatible stores need. Default true.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// PEM file of the CA (or CAs) that issued the store's TLS certificate, for an internal CA the host does not trust.
    /// The certificate chain is then validated against these roots only; the host name is still checked.
    /// </summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>True when the component is served by the S3 provider.</summary>
    public bool IsS3 => string.Equals(Provider, S3Provider, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Limits of the streamed upload endpoint.</summary>
public sealed class FileUploadOptions
{
    /// <summary>Largest accepted upload in bytes (default 100 MiB). Larger requests answer 413 before any byte is stored.</summary>
    public long MaxBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Minutes an uploaded file may wait to be referenced by a start or transition (default 60).</summary>
    public int TicketTtlMinutes { get; set; } = 60;
}
