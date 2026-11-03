# Python Task

## Purpose

`PythonTask` (`TaskType = 23`, JSON discriminator `"23"`) calls a named **handler** in the
domain's own Python runtime container. vNext acts as a wrapper: it builds the request from the
standard task mapping, calls the runtime with a versioned contract, applies the timeout and turns
the answer into a standard task result. The error boundary and the transition pipeline do not
know that the task was Python.

```
Orchestration ──(InputHandler → invoke → OutputHandler)──▶ Dapr invoke {app-id} POST v1/invoke
                                                              │
                                     python-runtime Deployment (opt-in, per domain)
                                     FROM ghcr.io/burgan-tech/vnext/python-runtime
                                     + domain handlers + pinned requirements
```

- **No Execution hop.** The Orchestration host calls the runtime directly from its own sidecar.
- **No Python in vNext.** No vNext image carries CPython, `pythonnet` or a Kubernetes client.
  A domain that does not use Python deploys nothing extra.
- **Code is in the image, not in the task.** Handlers and their packages are installed when the
  domain builds its runtime image; nothing is installed at container start.

## Task definition

```json
{
  "type": "23",
  "config": {
    "handler": "risk_score",
    "input": { "income": 85000 },
    "timeoutSeconds": 20
  }
}
```

| Field | Required | Contract |
| --- | --- | --- |
| `handler` | yes | Name of a handler registered in the runtime: `^[A-Za-z_][A-Za-z0-9_.-]{0,127}$`. |
| `input` | no | Any JSON value. The default input; an input mapping may replace it. At most 2 MiB as authored. |
| `timeoutSeconds` | no | Handler timeout, 1–50, default 30. Orchestration waits `timeoutSeconds + Workflow:PythonRuntime:TimeoutGraceSeconds`. |

### Mapping

Python tasks use the standard `IMapping` like every other task. `InputHandler` shapes the request by
calling `SetInput` on the task, `OutputHandler` reads the handler output from the task response:

```csharp
public class RiskScoreMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        ((PythonTask)task).SetInput(new { income = context.Instance.Data.income });
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = new { risk = context.Body?.data } });
}
```

Without a mapping the task result is the handler output as is.

## Writing handlers

A handler is a function decorated with `@handler("name")`. It receives the JSON input, and
optionally a read-only context, and returns a strict-JSON value:

```python
# handlers/calculations.py
from vnext_runtime import HandlerContext, handler

@handler("sum_values")
def sum_values(input):
    return {"total": sum(input["values"])}

@handler("risk_score")
def risk_score(input, context: HandlerContext):
    return {"score": min(100, input["income"] // 1000), "instanceId": context.instance_id}
```

The return value must pass `json.dumps(..., allow_nan=False)`. Convert NumPy and pandas values
with `.tolist()`, `.item()` or `.to_dict()`. An exception becomes a `handler_error` failure.

`HandlerContext` fields: `domain`, `workflow`, `workflow_version`, `instance_id`, `task_key`,
`correlation_id`, `timeout_ms`. Treat the input as raw instance data, including personal data.

### Domain image

```dockerfile
ARG VNEXT_PYTHON_RUNTIME=ghcr.io/burgan-tech/vnext/python-runtime:<version>
FROM ${VNEXT_PYTHON_RUNTIME}

USER root
COPY requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --require-hashes -r /tmp/requirements.txt && rm /tmp/requirements.txt
COPY handlers /opt/vnext-python/app/handlers
USER 65532:65532
```

Pin every requirement (preferably with hashes, `pip-compile --generate-hashes`) so the image is
reproducible and can be scanned before it ships. A complete example is in
`runtimes/python/example/`. Every module under the `handlers` package is imported at start
(`VNEXT_PYTHON_HANDLERS` names a different package).

## Runtime contract (version 1)

Every endpoint answers `application/json`.

### `POST v1/invoke`

Request (sent by Orchestration):

```json
{
  "contractVersion": "1",
  "handler": "risk_score",
  "input": { "income": 85000 },
  "timeoutMs": 20000,
  "context": {
    "domain": "core",
    "workflow": "loan-application",
    "workflowVersion": "1.2.0",
    "instanceId": "8f1c…",
    "taskKey": "calculate-risk",
    "correlationId": "…"
  }
}
```

Success (`200`):

```json
{ "contractVersion": "1", "status": "success", "output": { "score": 85 }, "durationMs": 12 }
```

Failure:

```json
{ "contractVersion": "1", "status": "error", "error": { "code": "handler_error", "message": "KeyError: 'income'" } }
```

| HTTP | `error.code` | Meaning |
| --- | --- | --- |
| 400 | `bad_request`, `unsupported_contract` | The body is not JSON / not an object, or `contractVersion` is not `1`. |
| 404 | `unknown_handler` | No handler with that name is registered in the image. |
| 413 | `request_too_large` | Body larger than `VNEXT_PYTHON_MAX_REQUEST_BYTES` (4 MiB). |
| 422 | `handler_error` | The handler raised. |
| 422 | `invalid_output`, `output_too_large` | The result is not strict JSON, or larger than `VNEXT_PYTHON_MAX_OUTPUT_BYTES` (4 MiB). |
| 504 | `timeout` | The handler did not finish within `timeoutMs`. |

On the vNext side a success makes `output` the task result. A failure is a failed task whose
`StatusCode` is the HTTP status and whose `ErrorMessage` is `"<code>: <message>"`, so error-boundary
rules can match on either. Two failures come from Orchestration itself: **503** when the domain
has no runtime (`Workflow:PythonRuntime:AppId` is empty, no call is made) and **408** when the
runtime did not answer within `timeoutSeconds + TimeoutGraceSeconds`.

### `GET v1/health` and `GET v1/manifest`

`health` answers `{"status":"ok"}` for liveness and readiness probes. `manifest` lists the
contract version, runtime version, Python version and the registered handler names.

## Configuration

Orchestration (`Workflow:PythonRuntime`, set by the Helm chart when the runtime is enabled):

| Key | Default | Meaning |
| --- | --- | --- |
| `AppId` | empty | Dapr app-id of the domain's Python runtime. Empty = no runtime; Python tasks fail with 503. |
| `InvokeMethod` | `v1/invoke` | Invoke route of the contract. |
| `TimeoutGraceSeconds` | `5` | Extra wait beyond the handler timeout so the runtime's own 504 arrives first. |

Runtime container (environment):

| Variable | Default | Meaning |
| --- | --- | --- |
| `VNEXT_PYTHON_PORT` | `8080` | Listen port (the Dapr app port). |
| `VNEXT_PYTHON_HANDLERS` | `handlers` | Package whose modules register the handlers. |
| `VNEXT_PYTHON_MAX_WORKERS` | `4` | Concurrent handler calls per pod. |
| `VNEXT_PYTHON_MAX_REQUEST_BYTES` / `VNEXT_PYTHON_MAX_OUTPUT_BYTES` | 4 MiB | Body limits. |

## Operating notes

- **Isolation.** Calls share one process per pod. A handler that leaks memory or keeps global state
  affects the calls after it. A timed-out call keeps its worker thread until it returns (Python
  cannot kill a thread), so keep handlers inside their timeout and size `MAX_WORKERS` and replicas
  for the slowest handler. CPU-bound work is limited by the GIL: scale with replicas.
- **Security.** Handler code is trusted domain code. Run the runtime with a read-only root
  filesystem, non-root user, dropped capabilities, no service-account token and a NetworkPolicy
  that admits only the Orchestration sidecar. Secrets come from the Dapr secret store or the pod
  environment, never from the task definition.
- **Timeout layering.** Handler `timeoutMs` < Orchestration wait (`+ TimeoutGraceSeconds`) <
  transition job budget.
- **Observability.** The call is a Dapr service invocation from Orchestration and is traced like
  any `daprservice` call (`Invoke.Prepare` span plus the sidecar's spans). Payloads are not logged
  on either side.

## References

- `src/BBT.Workflow.Domain/Definitions/Tasks/PythonTask.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Python/`
- `runtimes/python/` — base image, `vnext_runtime` package, example domain image, contract tests
