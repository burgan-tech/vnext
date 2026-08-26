using System.Net;
using System.Text;
using BBT.Workflow.Execution.Configuration;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace BBT.Workflow.Execution.Python.Containers;

internal sealed class KubernetesExecutionClient : IKubernetesExecutionClient
{
    private readonly IKubernetes _client;

    public KubernetesExecutionClient(PythonKubernetesOptions options)
    {
        var configuration = string.IsNullOrWhiteSpace(options.KubeConfigPath) &&
                            string.IsNullOrWhiteSpace(options.Context)
            ? KubernetesClientConfiguration.BuildDefaultConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile(
                options.KubeConfigPath,
                options.Context);
        _client = new Kubernetes(configuration);
    }

    public async Task CreateJobAsync(
        string namespaceName,
        V1Job job,
        CancellationToken cancellationToken)
    {
        await _client.BatchV1.CreateNamespacedJobAsync(
            job,
            namespaceName,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<V1Pod>> ListJobPodsAsync(
        string namespaceName,
        string jobName,
        CancellationToken cancellationToken)
    {
        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            namespaceName,
            labelSelector: $"job-name={jobName}",
            cancellationToken: cancellationToken);
        return pods.Items.ToList();
    }

    public async Task<(string Stdout, string Stderr)> AttachAsync(
        string namespaceName,
        string podName,
        string containerName,
        string inputJson,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var webSocket = await _client.WebSocketNamespacedPodAttachAsync(
            podName,
            namespaceName,
            containerName,
            stderr: true,
            stdin: true,
            stdout: true,
            tty: false,
            webSocketSubProtocol: WebSocketProtocol.V4BinaryWebsocketProtocol,
            cancellationToken: cancellationToken);
        using var demuxer = new StreamDemuxer(webSocket, ownsSocket: false);
        demuxer.Start();

        await using var stdoutStream = demuxer.GetStream(ChannelIndex.StdOut, null);
        await using var stderrStream = demuxer.GetStream(ChannelIndex.StdErr, null);
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budget = new SharedResponseBudget(maxResponseBytes);
        var stdoutTask = ReadBoundedAsync(stdoutStream, budget, reads.Token);
        var stderrTask = ReadBoundedAsync(stderrStream, budget, reads.Token);

        try
        {
            var payload = Encoding.UTF8.GetBytes(inputJson);
            await demuxer.Write(
                ChannelIndex.StdIn,
                payload,
                0,
                payload.Length,
                cancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask);
            return (stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            reads.Cancel();
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch
            {
                // Preserve the original transport, cancellation, or output-limit failure.
            }

            throw;
        }
    }

    public async Task DeleteJobAsync(
        string namespaceName,
        string jobName,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.BatchV1.DeleteNamespacedJobAsync(
                jobName,
                namespaceName,
                gracePeriodSeconds: 0,
                propagationPolicy: "Background",
                cancellationToken: cancellationToken);
        }
        catch (HttpOperationException exception)
            when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Creation may have been cancelled after the API server accepted the Job,
            // or TTL cleanup may have won the race. Either way the target is gone.
        }
    }

    public async Task CheckAvailabilityAsync(
        string namespaceName,
        CancellationToken cancellationToken)
    {
        await _client.BatchV1.ListNamespacedJobAsync(
            namespaceName,
            limit: 1,
            cancellationToken: cancellationToken);
        await _client.CoreV1.ListNamespacedPodAsync(
            namespaceName,
            limit: 1,
            cancellationToken: cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        SharedResponseBudget budget,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(output.ToArray());
            }

            if (!budget.TryConsume(read))
            {
                throw new PythonExecutionException(
                    "Python Kubernetes response exceeds the configured size limit.",
                    "output_limit_exceeded");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class SharedResponseBudget(int maxBytes)
    {
        private int _remaining = maxBytes;

        public bool TryConsume(int count)
        {
            var remaining = Interlocked.Add(ref _remaining, -count);
            return remaining >= 0;
        }
    }
}
