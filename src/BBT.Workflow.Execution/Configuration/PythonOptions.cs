namespace BBT.Workflow.Execution.Configuration;

/// <summary>
/// Configuration for trusted Python task execution.
/// </summary>
public sealed class PythonOptions
{
    public const string SectionName = "Python";

    public bool Enabled { get; set; }
    public string DefaultMode { get; set; } = PythonRuntimeModes.PythonNet;
    public string[] EnabledModes { get; set; } =
        [PythonRuntimeModes.PythonNet, PythonRuntimeModes.Process];
    public int MaxTimeoutSeconds { get; set; } = 50;
    public int MaxCodeBytes { get; set; } = 256 * 1024;
    public int MaxInputBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxOutputBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxStdoutBytes { get; set; } = 32 * 1024;
    public int MaxStderrBytes { get; set; } = 32 * 1024;
    public string[] AllowedModules { get; set; } = ["*"];
    public PythonNetOptions PythonNet { get; set; } = new();
    public PythonProcessOptions Process { get; set; } = new();
    public PythonContainerOptions Container { get; set; } = new();
}

public sealed class PythonNetOptions
{
    public string PythonDll { get; set; } = "libpython3.12.so.1.0";
    public string? PythonHome { get; set; } = "/usr";
    public string? PythonPath { get; set; } = "/opt/vnext-python/venv/lib/python3.12/site-packages";
    public string RunnerDirectory { get; set; } = "/opt/vnext-python";
    public int MaxConcurrency { get; set; } = 1;
}

public sealed class PythonProcessOptions
{
    public string PythonExecutable { get; set; } = "/opt/vnext-python/venv/bin/python";
    public string RunnerPath { get; set; } = "/opt/vnext-python/runner.py";
    public int MaxConcurrency { get; set; } = 2;
    public bool UsePrlimit { get; set; } = true;
    public string PrlimitExecutable { get; set; } = "/usr/bin/prlimit";
    public long MemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int CpuTimeSeconds { get; set; } = 45;
    public int OpenFiles { get; set; } = 256;
}

public sealed class PythonContainerOptions
{
    public string Driver { get; set; } = "docker";
    public string Endpoint { get; set; } = "unix:///var/run/docker.sock";
    public string ApiVersion { get; set; } = "v1.43";
    public string Image { get; set; } = "ghcr.io/burgan-tech/vnext/python-runner:latest";
    public string PullPolicy { get; set; } = "ifNotPresent";
    public string NetworkMode { get; set; } = "none";
    public int MaxConcurrency { get; set; } = 2;
    public long MemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public long NanoCpus { get; set; } = 1_000_000_000;
    public long PidsLimit { get; set; } = 128;
    public long TmpfsBytes { get; set; } = 64L * 1024 * 1024;
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public string? CaCertificatePath { get; set; }
    public PythonKubernetesOptions Kubernetes { get; set; } = new();
}

public sealed class PythonKubernetesOptions
{
    public string Namespace { get; set; } = "default";
    public string? KubeConfigPath { get; set; }
    public string? Context { get; set; }
    public string ContainerName { get; set; } = "python-runner";
    public string? RunnerServiceAccountName { get; set; }
    public string[] ImagePullSecrets { get; set; } = [];
    public int PodStartTimeoutSeconds { get; set; } = 30;
    public int PollIntervalMilliseconds { get; set; } = 250;
    public int JobTtlSecondsAfterFinished { get; set; } = 60;
    public string? RuntimeClassName { get; set; }
}

public static class PythonRuntimeModes
{
    public const string PythonNet = "pythonNet";
    public const string Process = "process";
    public const string Container = "container";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [PythonNet, Process, Container],
        StringComparer.OrdinalIgnoreCase);
}
