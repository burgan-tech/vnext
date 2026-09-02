using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Configuration;

public sealed class PythonOptionsValidator : IValidateOptions<PythonOptions>
{
    public ValidateOptionsResult Validate(string? name, PythonOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DefaultMode) ||
            !PythonRuntimeModes.All.Contains(options.DefaultMode))
        {
            failures.Add($"Python:DefaultMode '{options.DefaultMode}' is invalid.");
        }

        var enabledModes = options.EnabledModes ?? [];
        if (enabledModes.Length == 0 && options.Enabled)
        {
            failures.Add("Python:EnabledModes must contain at least one mode when Python is enabled.");
        }

        foreach (var mode in enabledModes)
        {
            if (!PythonRuntimeModes.All.Contains(mode))
            {
                failures.Add($"Python:EnabledModes contains invalid mode '{mode}'.");
            }
        }

        if (options.Enabled &&
            !enabledModes.Contains(options.DefaultMode, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add("Python:DefaultMode must also be present in Python:EnabledModes.");
        }

        if (options.MaxTimeoutSeconds is < 1 or > 50)
        {
            failures.Add("Python:MaxTimeoutSeconds must be between 1 and 50.");
        }

        ValidatePositive(options.MaxCodeBytes, "Python:MaxCodeBytes", failures);
        ValidatePositive(options.MaxInputBytes, "Python:MaxInputBytes", failures);
        ValidatePositive(options.MaxOutputBytes, "Python:MaxOutputBytes", failures);
        ValidatePositive(options.MaxStdoutBytes, "Python:MaxStdoutBytes", failures);
        ValidatePositive(options.MaxStderrBytes, "Python:MaxStderrBytes", failures);
        var protocolPayloadBytes = (long)options.MaxOutputBytes +
                                   options.MaxStdoutBytes +
                                   options.MaxStderrBytes;
        if (protocolPayloadBytes * 6 + 64 * 1024 > int.MaxValue)
        {
            failures.Add(
                "Python output/stdout/stderr limits are too large for the runner protocol envelope.");
        }
        ValidatePositive(options.PythonNet.MaxConcurrency, "Python:PythonNet:MaxConcurrency", failures);
        ValidatePositive(options.Process.MaxConcurrency, "Python:Process:MaxConcurrency", failures);
        ValidatePositive(options.Container.MaxConcurrency, "Python:Container:MaxConcurrency", failures);
        ValidatePositive(options.Process.MemoryBytes, "Python:Process:MemoryBytes", failures);
        ValidatePositive(options.Process.CpuTimeSeconds, "Python:Process:CpuTimeSeconds", failures);
        ValidatePositive(options.Process.OpenFiles, "Python:Process:OpenFiles", failures);
        ValidatePositive(options.Container.MemoryBytes, "Python:Container:MemoryBytes", failures);
        ValidatePositive(options.Container.NanoCpus, "Python:Container:NanoCpus", failures);
        ValidatePositive(options.Container.PidsLimit, "Python:Container:PidsLimit", failures);
        ValidatePositive(options.Container.TmpfsBytes, "Python:Container:TmpfsBytes", failures);
        ValidatePositive(
            options.Container.Kubernetes.PodStartTimeoutSeconds,
            "Python:Container:Kubernetes:PodStartTimeoutSeconds",
            failures);
        ValidatePositive(
            options.Container.Kubernetes.PollIntervalMilliseconds,
            "Python:Container:Kubernetes:PollIntervalMilliseconds",
            failures);

        if (options.Container.Kubernetes.JobTtlSecondsAfterFinished < 0)
        {
            failures.Add("Python:Container:Kubernetes:JobTtlSecondsAfterFinished cannot be negative.");
        }

        if (options.AllowedModules is not { Length: > 0 } ||
            options.AllowedModules.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("Python:AllowedModules must contain at least one non-empty module name.");
        }

        if (enabledModes.Contains(PythonRuntimeModes.PythonNet, StringComparer.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(options.PythonNet.PythonDll) ||
             string.IsNullOrWhiteSpace(options.PythonNet.PythonHome) ||
             string.IsNullOrWhiteSpace(options.PythonNet.PythonPath) ||
             string.IsNullOrWhiteSpace(options.PythonNet.RunnerDirectory)))
        {
            failures.Add(
                "Python.NET mode requires PythonDll, PythonHome, PythonPath, and RunnerDirectory.");
        }

        if (enabledModes.Contains(PythonRuntimeModes.Process, StringComparer.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(options.Process.PythonExecutable) ||
             string.IsNullOrWhiteSpace(options.Process.RunnerPath)))
        {
            failures.Add("Python process mode requires PythonExecutable and RunnerPath.");
        }

        if (enabledModes.Contains(PythonRuntimeModes.Process, StringComparer.OrdinalIgnoreCase) &&
            options.Process.UsePrlimit &&
            string.IsNullOrWhiteSpace(options.Process.PrlimitExecutable))
        {
            failures.Add("Python process mode requires PrlimitExecutable when UsePrlimit is enabled.");
        }

        var containerEnabled = enabledModes.Contains(
            PythonRuntimeModes.Container,
            StringComparer.OrdinalIgnoreCase);
        var dockerDriver = string.Equals(
            options.Container.Driver,
            "docker",
            StringComparison.OrdinalIgnoreCase);
        var kubernetesDriver = string.Equals(
            options.Container.Driver,
            "kubernetes",
            StringComparison.OrdinalIgnoreCase);

        if (containerEnabled &&
            ((!dockerDriver && !kubernetesDriver) || string.IsNullOrWhiteSpace(options.Container.Image)))
        {
            failures.Add("Python container mode requires the docker or kubernetes driver and a runner image.");
        }

        if (containerEnabled && dockerDriver &&
            string.IsNullOrWhiteSpace(options.Container.Endpoint))
        {
            failures.Add("Python Docker container mode requires an endpoint.");
        }

        if (containerEnabled && dockerDriver &&
            (!Uri.TryCreate(options.Container.Endpoint, UriKind.Absolute, out var endpoint) ||
             endpoint.Scheme is not ("unix" or "http" or "https")))
        {
            failures.Add("Python:Container:Endpoint must use unix, http, or https.");
        }

        if (containerEnabled &&
            string.IsNullOrWhiteSpace(options.Container.NetworkMode))
        {
            failures.Add("Python:Container:NetworkMode is required.");
        }

        if (containerEnabled && kubernetesDriver &&
            (string.IsNullOrWhiteSpace(options.Container.Kubernetes.Namespace) ||
             string.IsNullOrWhiteSpace(options.Container.Kubernetes.ContainerName)))
        {
            failures.Add(
                "Python Kubernetes container mode requires Namespace and ContainerName.");
        }

        if (options.Container.Kubernetes.ImagePullSecrets.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add(
                "Python:Container:Kubernetes:ImagePullSecrets cannot contain empty names.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePositive(int value, string key, ICollection<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{key} must be greater than zero.");
        }
    }

    private static void ValidatePositive(long value, string key, ICollection<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{key} must be greater than zero.");
        }
    }
}
