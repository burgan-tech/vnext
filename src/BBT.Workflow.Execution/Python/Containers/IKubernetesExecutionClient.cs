using k8s.Models;

namespace BBT.Workflow.Execution.Python.Containers;

internal interface IKubernetesExecutionClient : IDisposable
{
    Task CreateJobAsync(
        string namespaceName,
        V1Job job,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<V1Pod>> ListJobPodsAsync(
        string namespaceName,
        string jobName,
        CancellationToken cancellationToken);

    Task<(string Stdout, string Stderr)> AttachAsync(
        string namespaceName,
        string podName,
        string containerName,
        string inputJson,
        int maxResponseBytes,
        CancellationToken cancellationToken);

    Task DeleteJobAsync(
        string namespaceName,
        string jobName,
        CancellationToken cancellationToken);

    Task CheckAvailabilityAsync(
        string namespaceName,
        CancellationToken cancellationToken);
}
