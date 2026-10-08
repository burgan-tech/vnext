namespace BBT.Workflow.Files;

public sealed class FileStorageOptions
{
    public const string Section = "FileStorage";

    /// <summary>Prepended to every object key (e.g. <c>vnext-runtime/</c> in a shared bucket). Never change it after the first write.</summary>
    public string KeyPrefix { get; set; } = string.Empty;
}
