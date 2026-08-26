using System.Text.Json;

namespace BBT.Workflow.Execution.Python;

public sealed record PythonExecutionRequest(
    string Script,
    string Location,
    string InputJson,
    TimeSpan Timeout);

public sealed record PythonExecutionResult(
    string OutputJson,
    string Stdout,
    string Stderr,
    string RuntimeVersion,
    bool StdoutTruncated,
    bool StderrTruncated);

internal sealed class PythonRunnerRequest
{
    public required string Script { get; init; }
    public required string Location { get; init; }
    public required JsonElement Input { get; init; }
    public required IReadOnlyList<string> AllowedModules { get; init; }
    public required int MaxOutputBytes { get; init; }
    public required int MaxStdoutBytes { get; init; }
    public required int MaxStderrBytes { get; init; }
}

internal sealed class PythonRunnerResponse
{
    public bool Success { get; init; }
    public string? OutputJson { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public bool StdoutTruncated { get; init; }
    public bool StderrTruncated { get; init; }
    public string? Error { get; init; }
    public string? ExceptionType { get; init; }
}

public interface IPythonExecutionRuntime
{
    string Mode { get; }

    Task<PythonExecutionResult> ExecuteAsync(
        PythonExecutionRequest request,
        CancellationToken cancellationToken = default);

    Task CheckAvailabilityAsync(CancellationToken cancellationToken = default);
}

public interface IPythonRuntimeRegistry
{
    IPythonExecutionRuntime GetRequiredRuntime(string mode);
    bool IsEnabled(string mode);
    Task CheckEnabledRuntimesAsync(CancellationToken cancellationToken = default);
}

public sealed class PythonExecutionException(
    string message,
    string reason,
    string? exceptionType = null,
    string? runtimeVersion = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Reason { get; } = reason;
    public string? PythonExceptionType { get; } = exceptionType;
    public string? RuntimeVersion { get; } = runtimeVersion;
}
