using Microsoft.Extensions.Options;

namespace BBT.Workflow.Files;

/// <summary>Fails the start on a non-positive streamed-upload limit.</summary>
public sealed class FileStorageOptionsValidator : IValidateOptions<FileStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FileStorageOptions options)
    {
        var failures = new List<string>();
        if (options.Upload.MaxBytes <= 0)
            failures.Add($"{FileStorageOptions.Section}:Upload:MaxBytes must be positive.");
        if (options.Upload.TicketTtlMinutes <= 0)
            failures.Add($"{FileStorageOptions.Section}:Upload:TicketTtlMinutes must be positive.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
