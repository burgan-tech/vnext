using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Python.Containers;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class KubernetesContainerExecutionDriverTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesHardenedJobAttachesAndAlwaysDeletesIt()
    {
        var client = new StubKubernetesExecutionClient();
        var options = CreateOptions();
        var driver = new KubernetesContainerExecutionDriver(
            options,
            client,
            NullLogger<KubernetesContainerExecutionDriver>.Instance);
        var request = CreateRequest();

        var result = await driver.ExecuteAsync(request);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("{\"success\":true}");
        client.AttachedInput.ShouldBe(request.InputJson);
        client.DeletedJobName.ShouldBe(client.CreatedJob!.Metadata.Name);

        var job = client.CreatedJob;
        job.Spec.BackoffLimit.ShouldBe(0);
        job.Spec.ActiveDeadlineSeconds.ShouldBe(5);
        job.Spec.TtlSecondsAfterFinished.ShouldBe(60);
        var pod = job.Spec.Template.Spec;
        pod.AutomountServiceAccountToken.ShouldBe(false);
        pod.RestartPolicy.ShouldBe("Never");
        pod.ServiceAccountName.ShouldBe("python-runner");
        pod.SecurityContext.RunAsNonRoot.ShouldBe(true);
        var container = pod.Containers.Single();
        container.Stdin.ShouldBe(true);
        container.StdinOnce.ShouldBe(true);
        container.Tty.ShouldBe(false);
        container.SecurityContext.ReadOnlyRootFilesystem.ShouldBe(true);
        container.SecurityContext.AllowPrivilegeEscalation.ShouldBe(false);
        container.SecurityContext.Capabilities.Drop.ShouldContain("ALL");
        container.Args[0].ShouldBe("--stdin-bytes");
        container.Args[1].ShouldBe(System.Text.Encoding.UTF8.GetByteCount(request.InputJson).ToString());
        container.Resources.Limits["cpu"].ToString().ShouldBe("1");
        container.Resources.Limits["memory"].ToString().ShouldBe("2147483648");
        pod.Volumes.Single().EmptyDir.Medium.ShouldBe("Memory");
        job.Spec.Template.Metadata.Labels["vnext.burgan.tech/python-network"].ShouldBe("none");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStillDeletesJob()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var client = new StubKubernetesExecutionClient { BlockAttach = true };
        var driver = new KubernetesContainerExecutionDriver(
            CreateOptions(),
            client,
            NullLogger<KubernetesContainerExecutionDriver>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            driver.ExecuteAsync(CreateRequest(), cancellation.Token));

        client.DeletedJobName.ShouldBe(client.CreatedJob!.Metadata.Name);
    }

    private static PythonContainerOptions CreateOptions() => new()
    {
        Image = "runner:test",
        PullPolicy = "ifNotPresent",
        Kubernetes = new PythonKubernetesOptions
        {
            Namespace = "workflow",
            ContainerName = "python-runner",
            RunnerServiceAccountName = "python-runner",
            ImagePullSecrets = ["registry"],
            PodStartTimeoutSeconds = 2,
            PollIntervalMilliseconds = 1,
            JobTtlSecondsAfterFinished = 60
        }
    };

    private static ContainerExecutionRequest CreateRequest() => new(
        "runner:test",
        "{\"script\":\"def main(input): return input\",\"input\":{}}",
        TimeSpan.FromSeconds(5),
        2L * 1024 * 1024 * 1024,
        1_000_000_000,
        128,
        64L * 1024 * 1024,
        "none",
        1024,
        new Dictionary<string, string> { ["PIP_NO_INDEX"] = "1" });

    private sealed class StubKubernetesExecutionClient : IKubernetesExecutionClient
    {
        private int _listCount;

        public V1Job? CreatedJob { get; private set; }
        public string? AttachedInput { get; private set; }
        public string? DeletedJobName { get; private set; }
        public bool BlockAttach { get; init; }

        public Task CreateJobAsync(
            string namespaceName,
            V1Job job,
            CancellationToken cancellationToken)
        {
            namespaceName.ShouldBe("workflow");
            CreatedJob = job;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<V1Pod>> ListJobPodsAsync(
            string namespaceName,
            string jobName,
            CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _listCount) == 1;
            var state = running
                ? null
                : new V1ContainerState
                {
                    Terminated = new V1ContainerStateTerminated { ExitCode = 0 }
                };
            IReadOnlyList<V1Pod> pods =
            [
                new V1Pod
                {
                    Metadata = new V1ObjectMeta { Name = "runner-pod" },
                    Status = new V1PodStatus
                    {
                        Phase = running ? "Running" : "Succeeded",
                        ContainerStatuses = running
                            ? []
                            :
                            [
                                new V1ContainerStatus
                                {
                                    Name = "python-runner",
                                    State = state
                                }
                            ]
                    }
                }
            ];
            return Task.FromResult(pods);
        }

        public async Task<(string Stdout, string Stderr)> AttachAsync(
            string namespaceName,
            string podName,
            string containerName,
            string inputJson,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            AttachedInput = inputJson;
            if (BlockAttach)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return ("{\"success\":true}", string.Empty);
        }

        public Task DeleteJobAsync(
            string namespaceName,
            string jobName,
            CancellationToken cancellationToken)
        {
            DeletedJobName = jobName;
            return Task.CompletedTask;
        }

        public Task CheckAvailabilityAsync(
            string namespaceName,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
