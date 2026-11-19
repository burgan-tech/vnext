using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// The Dapr app API token the local-delivery guard compares against the <c>dapr-api-token</c> header.
/// Read once, at construction, from the mounted file named by
/// <see cref="RuntimeCallerOptions.AppApiTokenFile"/>: never from configuration or the environment,
/// because flow scripts can read both. Singleton; rotation restarts the host.
/// </summary>
public sealed class DaprAppApiToken
{
    /// <summary>Reads the file named by <see cref="RuntimeCallerOptions.AppApiTokenFile"/>, if any.</summary>
    /// <exception cref="InvalidOperationException">The file is set but missing, unreadable or empty.</exception>
    public DaprAppApiToken(IOptions<RuntimeCallerOptions> options)
        => Value = DaprAppApiTokenLoader.Load(options.Value.AppApiTokenFile);

    /// <summary>Test seam: a token over an in-memory value.</summary>
    internal DaprAppApiToken(string? value) => Value = value;

    /// <summary>The token; null when no file is configured. Never log it.</summary>
    public string? Value { get; }

    /// <summary>True when a token file was configured and loaded.</summary>
    public bool IsConfigured => Value is not null;

    /// <summary>Never returns the token value.</summary>
    public override string ToString() => IsConfigured ? "DaprAppApiToken(configured)" : "DaprAppApiToken(not configured)";
}

/// <summary>Reads the app API token file; shared by <see cref="DaprAppApiToken"/> and the startup validator.</summary>
internal static class DaprAppApiTokenLoader
{
    /// <summary>Null when <paramref name="path"/> is null or blank; the trimmed content otherwise.</summary>
    /// <exception cref="InvalidOperationException">
    /// The file is missing, unreadable or empty after trimming. The message names the path, never the content.
    /// </exception>
    public static string? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var content = ReadTrimmed(path);
        if (content.Length == 0)
            throw new InvalidOperationException($"The Dapr app API token file '{path}' is empty.");

        return content;
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The Dapr app API token file '{path}' cannot be read: {exception.GetType().Name}.");
        }
    }
}
