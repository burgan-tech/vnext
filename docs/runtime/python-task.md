# Python Task

## Purpose

`PythonTask` is the built-in trusted-code task (`TaskType = 22`, JSON discriminator `"22"`,
Execution route `python`). Orchestration sends the task's fixed JSON input in a strongly typed
binding to the Execution service. Execution invokes CPython through
one explicitly selected runtime: `pythonNet`, `process`, or `container`.

Python code must define exactly one callable entry point named `main`:

```python
def main(input):
    return {"total": sum(input["values"])}
```

Python tasks do not execute Orchestration `InputHandler`/`OutputHandler` mappings, and their
scripts do not define Python `input_handler` or `output_handler` functions. The only executable
contract is `main(input)` in the Execution service. Put the complete JSON argument in
`PythonTask.config.input`; the strict JSON return becomes the standard task result.

The argument and return value cross the runtime boundary as JSON. Code and input are never
concatenated. The return value must be accepted by `json.dumps(..., allow_nan=False)`. Convert
NumPy and pandas objects in the workflow script with `.tolist()`, `.item()`, or `.to_dict()`.

## Task contract

```json
{
  "type": "22",
  "config": {
    "script": {
      "location": "inline.py",
      "code": "def main(input):\n    return {'total': sum(input['values'])}",
      "type": "LOC",
      "encoding": "NAT"
    },
    "executionMode": "process",
    "input": { "values": [2, 3, 5] },
    "timeoutSeconds": 30
  }
}
```

| Field | Required | Contract |
| --- | --- | --- |
| `script` | yes | Existing `ScriptCode` shape. Only `NAT` and `B64` are accepted. `REF`, global mappings, and filesystem paths are rejected. `location` is diagnostic metadata, not a file to load. |
| `executionMode` | no | `pythonNet`, `process`, or `container`. Defaults to `Python:DefaultMode`. A disabled or unavailable mode fails; it never falls back to another mode. |
| `input` | no | Any JSON value, including null, scalar, array, or object. |
| `timeoutSeconds` | no | Default 30; bounded by `Python:MaxTimeoutSeconds` (default 50), which must remain below the Dapr invocation timeout. |

Default limits are 256 KiB of decoded code, 2 MiB of input, 2 MiB of output, and 32 KiB
each for captured stdout and stderr. All limits are configurable in the Execution host.

## Runtime modes

| Mode | Isolation and limits | Default concurrency |
| --- | --- | --- |
| `pythonNet` | In-process CPython 3.12 under the GIL, with a fresh Python scope per invocation. Timeout interruption is best effort; native extensions cannot be forcibly stopped and memory/CPU limits are not guaranteed. | 1 |
| `process` | `python -I runner.py` with the shared JSON protocol. Timeout or cancellation kills the process tree. Linux uses `prlimit` for CPU, address-space, and open-file limits. | 2 |
| `container` | A new hardened runner container per invocation, controlled by the explicitly selected Docker Engine or Kubernetes driver. It is always removed in `finally`. | 2 |

The container driver defaults to 1 CPU, 2 GiB memory, 128 PIDs, a read-only root filesystem,
tmpfs `/tmp`, UID/GID 65532, dropped capabilities, `no-new-privileges`, no host mounts, and
network `none`. Operators may select a pre-created Docker network. Container execution is
opt-in; the normal development Compose file does not mount the Docker socket. Use
`etc/docker/docker-compose.python-container.yml` only in an explicitly approved environment.

The Kubernetes driver creates one `batch/v1` Job per invocation with `backoffLimit: 0`, an
active deadline, a memory-backed `/tmp`, no service-account token in the runner pod, and the same
container security/resource settings. It waits for the Job pod, sends the JSON request over the
Kubernetes `pods/attach` streaming API, reads stdout/stderr separately, then deletes the Job in a
`finally` path. The runner reads an explicit UTF-8 byte count, so code and input are not stored in
a ConfigMap, Secret, command line, or environment variable.

Kubernetes does not expose a portable per-Pod PID limit. `PidsLimit` is recorded on the Job as
requested policy metadata, but operators must enforce it through the node/container runtime or a
compatible admission/runtime policy. `NetworkMode=none` is expressed through runner labels; the
Helm chart installs a default-deny NetworkPolicy for those pods. Named network policies are
operator-defined and require a NetworkPolicy-capable CNI.

## Packages and imports

Both the Execution venv and `ghcr.io/burgan-tech/vnext/python-runner:<version>` install the
same hash-locked `execution/python/requirements.lock`. The initial package set is NumPy 2.5.1,
pandas 3.0.5, and scikit-learn 1.9.0 plus their locked transitive dependencies. Runtime package
installation is disabled (`PIP_NO_INDEX=1`); update and review the lock file at build time.

`Python:AllowedModules` defaults to `["*"]`. A narrower list is enforced by the shared runner
bootstrap in every mode, including dynamic imports. This is an administrative dependency policy,
not a security sandbox. Python task source is trusted platform code. Do not use Python.NET for
untrusted input; use an independently secured isolation boundary outside this feature.

## Execution configuration

```json
{
  "Python": {
    "Enabled": true,
    "DefaultMode": "pythonNet",
    "EnabledModes": ["pythonNet", "process"],
    "MaxTimeoutSeconds": 50,
    "MaxCodeBytes": 262144,
    "MaxInputBytes": 2097152,
    "MaxOutputBytes": 2097152,
    "MaxStdoutBytes": 32768,
    "MaxStderrBytes": 32768,
    "AllowedModules": ["*"],
    "PythonNet": {
      "PythonDll": "libpython3.12.so.1.0",
      "PythonHome": "/usr",
      "PythonPath": "/opt/vnext-python/venv/lib/python3.12/site-packages",
      "RunnerDirectory": "/opt/vnext-python",
      "MaxConcurrency": 1
    },
    "Process": {
      "PythonExecutable": "/opt/vnext-python/venv/bin/python",
      "RunnerPath": "/opt/vnext-python/runner.py",
      "MaxConcurrency": 2,
      "UsePrlimit": true
    },
    "Container": {
      "Driver": "kubernetes",
      "Image": "ghcr.io/burgan-tech/vnext/python-runner:<version>",
      "NetworkMode": "none",
      "Kubernetes": {
        "Namespace": "workflow",
        "ContainerName": "python-runner",
        "RunnerServiceAccountName": null,
        "ImagePullSecrets": [],
        "PodStartTimeoutSeconds": 30,
        "PollIntervalMilliseconds": 250,
        "JobTtlSecondsAfterFinished": 60,
        "RuntimeClassName": null
      }
    }
  }
}
```

Active modes are checked during Execution startup and readiness: the interpreter and locked
packages must import. Docker mode checks its Engine endpoint and image; Kubernetes mode checks
API access in the configured namespace. A driver that is unavailable never falls back to the
other container driver.

For Kubernetes, the Execution service account needs namespace-scoped access to create/get/list/
watch/delete Jobs, get/list/watch Pods, and create/get `pods/attach`. The Helm chart can create a
dedicated Execution service account, Role, and RoleBinding. The short-lived runner pod always has
`automountServiceAccountToken: false`, even when a runner service-account name is supplied for
image-pull or admission policy purposes.

## Observability and failures

The invoker records task type, execution mode, duration, outcome, and runtime version in
activities/metrics. Script, input, and output values are never emitted. Captured stdout/stderr
are size-limited; their contents are not emitted, and Debug logs contain only byte counts.

Syntax errors, a missing/non-callable `main`, Python exceptions, non-JSON results, NaN, timeout,
cancellation, output overflow, disabled modes, and runtime unavailability return a failed standard
task response. Orchestration therefore sends the failure through the existing task and error-boundary
flow; no transition-pipeline behavior is special-cased for Python.

## References

- `src/BBT.Workflow.Domain/Definitions/Tasks/PythonTask.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Remote/PythonTaskExecutor.cs`
- `src/BBT.Workflow.Execution/Python/`
- `src/BBT.Workflow.Execution/Python/Containers/KubernetesContainerExecutionDriver.cs`
- `src/BBT.Workflow.Execution/Invokers/PythonTaskInvoker.cs`
- `execution/python/`
- `etc/docker/docker-compose.python-container.yml`
