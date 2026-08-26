using System.Globalization;
using System.Text;
using BBT.Workflow.Execution.Configuration;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python.Containers;

/// <summary>
/// Executes a Python request in a short-lived Kubernetes Job and streams the
/// request through the pod attach API. The Job and its pod are deleted after
/// every invocation, including timeout and cancellation paths.
/// </summary>
public sealed class KubernetesContainerExecutionDriver : IContainerExecutionDriver, IDisposable
{
    private const string RunnerLabel = "vnext.burgan.tech/python-runner";
    private const string NetworkLabel = "vnext.burgan.tech/python-network";
    private readonly PythonContainerOptions _options;
    private readonly Lazy<IKubernetesExecutionClient> _client;
    private readonly ILogger<KubernetesContainerExecutionDriver> _logger;

    public KubernetesContainerExecutionDriver(
        IOptions<PythonOptions> options,
        ILogger<KubernetesContainerExecutionDriver> logger)
        : this(options.Value.Container, logger)
    {
    }

    private KubernetesContainerExecutionDriver(
        PythonContainerOptions options,
        ILogger<KubernetesContainerExecutionDriver> logger)
    {
        _options = options;
        _logger = logger;
        _client = new Lazy<IKubernetesExecutionClient>(
            () => new KubernetesExecutionClient(_options.Kubernetes),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal KubernetesContainerExecutionDriver(
        PythonContainerOptions options,
        IKubernetesExecutionClient client,
        ILogger<KubernetesContainerExecutionDriver> logger)
    {
        _options = options;
        _client = new Lazy<IKubernetesExecutionClient>(() => client);
        _logger = logger;
    }

    public string Name => "kubernetes";

    public async Task<ContainerExecutionResult> ExecuteAsync(
        ContainerExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var jobName = $"vnext-python-{Guid.NewGuid():N}";
        var jobMayExist = false;
        try
        {
            var job = CreateJob(jobName, request);
            jobMayExist = true;
            await _client.Value.CreateJobAsync(
                _options.Kubernetes.Namespace,
                job,
                timeout.Token);

            var pod = await WaitForRunningPodAsync(jobName, timeout.Token);
            var output = await _client.Value.AttachAsync(
                _options.Kubernetes.Namespace,
                pod.Metadata.Name,
                _options.Kubernetes.ContainerName,
                request.InputJson,
                request.MaxResponseBytes,
                timeout.Token);
            var exitCode = await WaitForExitCodeAsync(jobName, timeout.Token);

            return new ContainerExecutionResult(exitCode, output.Stdout, output.Stderr);
        }
        finally
        {
            if (jobMayExist)
            {
                await TryDeleteJobAsync(jobName);
            }
        }
    }

    public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        _client.Value.CheckAvailabilityAsync(_options.Kubernetes.Namespace, cancellationToken);

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    internal V1Job CreateJob(string jobName, ContainerExecutionRequest request)
    {
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "vnext-python-runner",
            ["app.kubernetes.io/component"] = "python-runner",
            [RunnerLabel] = "true",
            [NetworkLabel] = ToLabelValue(request.NetworkMode)
        };
        var resources = new V1ResourceRequirements
        {
            Limits = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new($"{request.NanoCpus.ToString(CultureInfo.InvariantCulture)}n"),
                ["memory"] = new(request.MemoryBytes.ToString(CultureInfo.InvariantCulture))
            },
            Requests = new Dictionary<string, ResourceQuantity>
            {
                ["cpu"] = new($"{request.NanoCpus.ToString(CultureInfo.InvariantCulture)}n"),
                ["memory"] = new(request.MemoryBytes.ToString(CultureInfo.InvariantCulture))
            }
        };
        var container = new V1Container
        {
            Name = _options.Kubernetes.ContainerName,
            Image = request.Image,
            ImagePullPolicy = NormalizePullPolicy(_options.PullPolicy),
            Args =
            [
                "--stdin-bytes",
                Encoding.UTF8.GetByteCount(request.InputJson).ToString(CultureInfo.InvariantCulture)
            ],
            Env = request.Environment
                .Select(pair => new V1EnvVar { Name = pair.Key, Value = pair.Value })
                .ToList(),
            Stdin = true,
            StdinOnce = true,
            Tty = false,
            Resources = resources,
            SecurityContext = new V1SecurityContext
            {
                AllowPrivilegeEscalation = false,
                Capabilities = new V1Capabilities { Drop = ["ALL"] },
                Privileged = false,
                ReadOnlyRootFilesystem = true,
                RunAsGroup = 65532,
                RunAsNonRoot = true,
                RunAsUser = 65532,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
            },
            VolumeMounts =
            [
                new V1VolumeMount
                {
                    Name = "tmp",
                    MountPath = "/tmp"
                }
            ]
        };
        var podSpec = new V1PodSpec
        {
            AutomountServiceAccountToken = false,
            Containers = [container],
            ImagePullSecrets = _options.Kubernetes.ImagePullSecrets
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new V1LocalObjectReference { Name = name })
                .ToList(),
            RestartPolicy = "Never",
            RuntimeClassName = NullIfWhiteSpace(_options.Kubernetes.RuntimeClassName),
            ServiceAccountName = NullIfWhiteSpace(_options.Kubernetes.RunnerServiceAccountName),
            SecurityContext = new V1PodSecurityContext
            {
                FsGroup = 65532,
                RunAsGroup = 65532,
                RunAsNonRoot = true,
                RunAsUser = 65532,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
            },
            TerminationGracePeriodSeconds = 1,
            Volumes =
            [
                new V1Volume
                {
                    Name = "tmp",
                    EmptyDir = new V1EmptyDirVolumeSource
                    {
                        Medium = "Memory",
                        SizeLimit = new ResourceQuantity(
                            request.TmpfsBytes.ToString(CultureInfo.InvariantCulture))
                    }
                }
            ]
        };

        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                NamespaceProperty = _options.Kubernetes.Namespace,
                Labels = labels,
                Annotations = new Dictionary<string, string>
                {
                    ["vnext.burgan.tech/requested-pids-limit"] =
                        request.PidsLimit.ToString(CultureInfo.InvariantCulture)
                }
            },
            Spec = new V1JobSpec
            {
                ActiveDeadlineSeconds = Math.Max(1, (long)Math.Ceiling(request.Timeout.TotalSeconds)),
                BackoffLimit = 0,
                TtlSecondsAfterFinished = _options.Kubernetes.JobTtlSecondsAfterFinished,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = labels,
                        Annotations = new Dictionary<string, string>
                        {
                            ["dapr.io/enabled"] = "false",
                            ["vnext.burgan.tech/requested-pids-limit"] =
                                request.PidsLimit.ToString(CultureInfo.InvariantCulture)
                        }
                    },
                    Spec = podSpec
                }
            }
        };
    }

    private async Task<V1Pod> WaitForRunningPodAsync(
        string jobName,
        CancellationToken cancellationToken)
    {
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(_options.Kubernetes.PodStartTimeoutSeconds));

        while (true)
        {
            var pods = await _client.Value.ListJobPodsAsync(
                _options.Kubernetes.Namespace,
                jobName,
                startupTimeout.Token);
            var pod = pods.FirstOrDefault();
            if (pod is not null &&
                string.Equals(pod.Status?.Phase, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return pod;
            }

            if (pod is not null &&
                string.Equals(pod.Status?.Phase, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonExecutionException(
                    BuildPodFailureMessage(pod),
                    "container_failed");
            }

            await Task.Delay(
                _options.Kubernetes.PollIntervalMilliseconds,
                startupTimeout.Token);
        }
    }

    private async Task<int> WaitForExitCodeAsync(
        string jobName,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pods = await _client.Value.ListJobPodsAsync(
                _options.Kubernetes.Namespace,
                jobName,
                cancellationToken);
            var pod = pods.FirstOrDefault();
            var terminated = pod?.Status?.ContainerStatuses?
                .FirstOrDefault(status =>
                    string.Equals(
                        status.Name,
                        _options.Kubernetes.ContainerName,
                        StringComparison.Ordinal))?
                .State?.Terminated;
            if (terminated is not null)
            {
                return terminated.ExitCode;
            }

            if (pod is not null &&
                string.Equals(pod.Status?.Phase, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonExecutionException(
                    BuildPodFailureMessage(pod),
                    "container_failed");
            }

            await Task.Delay(
                _options.Kubernetes.PollIntervalMilliseconds,
                cancellationToken);
        }
    }

    private async Task TryDeleteJobAsync(string jobName)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _client.Value.DeleteJobAsync(
                _options.Kubernetes.Namespace,
                jobName,
                cleanupTimeout.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete Python Kubernetes Job {JobName} in namespace {Namespace}",
                jobName,
                _options.Kubernetes.Namespace);
        }
    }

    private static string BuildPodFailureMessage(V1Pod pod)
    {
        var status = pod.Status;
        var reason = status?.Reason;
        var message = status?.Message;
        return $"Python runner pod '{pod.Metadata.Name}' failed" +
               (string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})") +
               (string.IsNullOrWhiteSpace(message) ? "." : $": {message}");
    }

    private static string NormalizePullPolicy(string value) =>
        value.ToLowerInvariant() switch
        {
            "always" => "Always",
            "never" => "Never",
            _ => "IfNotPresent"
        };

    private static string ToLabelValue(string value)
    {
        var normalized = new string(value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray())
            .Trim('-', '_', '.');
        if (normalized.Length == 0)
        {
            return "none";
        }

        return normalized.Length <= 63 ? normalized : normalized[..63].TrimEnd('-', '_', '.');
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
