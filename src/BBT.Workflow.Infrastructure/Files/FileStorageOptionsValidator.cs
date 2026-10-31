using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>Fails the start on a store that cannot work: an unknown provider, an <c>s3</c> store without a bucket, a non-positive upload limit.</summary>
public sealed class FileStorageOptionsValidator : IValidateOptions<FileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options)
    {
        var failures = new List<string>();
        foreach (var (component, store) in options.Stores)
        {
            if (!store.IsS3 && !string.Equals(store.Provider, FileStoreOptions.DaprProvider, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{FileStorageOptions.Section}:Stores:{component}:Provider must be 'dapr' or 's3', not '{store.Provider}'.");
            if (store.IsS3 && string.IsNullOrWhiteSpace(store.Bucket))
                failures.Add($"{FileStorageOptions.Section}:Stores:{component}:Bucket is required for the s3 provider.");
            if (store.IsS3 && !string.IsNullOrWhiteSpace(store.ServiceUrl) && !Uri.TryCreate(store.ServiceUrl, UriKind.Absolute, out _))
                failures.Add($"{FileStorageOptions.Section}:Stores:{component}:ServiceUrl is not an absolute URL.");
            if (store.IsS3 && !string.IsNullOrWhiteSpace(store.CaCertificatePath) && !File.Exists(store.CaCertificatePath))
                failures.Add($"{FileStorageOptions.Section}:Stores:{component}:CaCertificatePath '{store.CaCertificatePath}' does not exist.");
        }
        if (options.Upload.MaxBytes <= 0)
            failures.Add($"{FileStorageOptions.Section}:Upload:MaxBytes must be positive.");
        if (options.Upload.TicketTtlMinutes <= 0)
            failures.Add($"{FileStorageOptions.Section}:Upload:TicketTtlMinutes must be positive.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
