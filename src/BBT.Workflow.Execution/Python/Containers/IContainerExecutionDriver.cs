namespace BBT.Workflow.Execution.Python.Containers;

public sealed record ContainerExecutionRequest(
    string Image,
    string InputJson,
    TimeSpan Timeout,
    long MemoryBytes,
    long NanoCpus,
    long PidsLimit,
    long TmpfsBytes,
    string NetworkMode,
    int MaxResponseBytes,
    IReadOnlyDictionary<string, string> Environment);

public sealed record ContainerExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr);

public interface IContainerExecutionDriver
{
    string Name { get; }

    Task<ContainerExecutionResult> ExecuteAsync(
        ContainerExecutionRequest request,
        CancellationToken cancellationToken = default);

    Task CheckAvailabilityAsync(CancellationToken cancellationToken = default);
}
