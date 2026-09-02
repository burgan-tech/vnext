# Spec: Orchestration → Execution gRPC Transport (+ DaprServiceTask decision)

Date: 2026-08-26 · Status: approved for planning · Owner: platform

## The ask

1. `DaprServiceTask` should always communicate over gRPC.
2. Orchestration should call vNext Execution over gRPC.
3. (Deferred by user) Move the remaining Dapr communications to gRPC — to be evaluated
   after 1 and 2 land.

## Evidence gathered before deciding

All verified against primary sources on 2026-08-26 — none of this is assumed:

- **Dapr v1.15 runtime, `pkg/api/grpc/grpc.go`, `InvokeService`:** two deprecation
  notices in the shipped code:
  - `"InvokeService is deprecated and will be removed in the future, please use proxy mode instead."`
  - `"Invocation path of gRPC -> HTTP is deprecated and will be removed in the future."`
- **Same function, HTTP-target semantics:** a non-2xx from an HTTP target becomes a
  gRPC *error* (`ErrorFromHTTPResponseCode`); the HTTP status survives only as the
  `dapr-http-status` response header. Response bodies of failures ride the error message.
- **Dapr .NET SDK 1.17.9 (`DaprClientGrpc.cs`):** `InvokeMethodAsync`/
  `InvokeMethodWithResponseAsync` → `httpClient.SendAsync` (HTTP API);
  `InvokeMethodGrpcAsync` → `Client.InvokeServiceAsync` (the **deprecated** API above).
  `DaprClient.CreateInvocationInvoker(appId, daprEndpoint, daprApiToken, grpcChannelOptions)`
  exists — the SDK's proxy-mode entry point.
- **`dapr.io/app-protocol` governs only sidecar→app delivery** (Dapr reference: "the
  protocol Dapr uses to communicate with your app"). It does not affect how the app
  calls its sidecar, and sidecar↔sidecar is always gRPC.
- **Execution's entire Dapr-inbound surface is ONE endpoint:**
  `POST api/v{version}/execution/invoke/{type}/{key}` (`ExecutionController`). No
  `[Topic]` subscriptions, no job callbacks, no other inbound service invocation.
- **Current wire contract:** request = `TaskInvokeRequest { Envelope, TraceContext }`
  as JSON; response = `TaskInvokeResponse { Success, ErrorMessage, Result, ExecutionDurationMs }`
  as JSON. `TaskEnvelope.Binding` is a `JsonElement`; the result carries
  `object? Data` — the contract is JSON-shaped through and through.
- Orchestration's client is `RemoteInvokerService` (`ExecutionApi:AppId`,
  `ExecutionApi:InvocationTimeoutSeconds=60`, no transport retry by design — the
  sidecar resiliency policy is circuit-breaker-only, documented in the class remarks).

## Decision 1 — DaprServiceTask stays on the HTTP API (decision record, no code)

"Always gRPC" for `DaprServiceTask` means running its outbound hop over an API Dapr has
deprecated *and* over the specific sub-path (gRPC caller → HTTP target) whose removal is
already announced. `DaprServiceTask`'s targets are external HTTP apps, and its public
contract — free HTTP verb, query string, request/response headers, `StatusCode`,
`ReasonPhrase`, `AcceptedStatusCodes` — is exactly what that path degrades: every non-2xx
would surface as an `RpcException` with the status hidden in `dapr-http-status`.

**Verdict: not implementable in a supported form today.** The deliverable is a decision
record in `docs/runtime/dapr-invocation-transport.md` so the question is answered once,
with the runtime-source evidence inline. Re-open only if/when targets speak gRPC — then
per-target proxy mode is the road, and item 3 builds the exact machinery it would reuse.

## Decision 2 — Orchestration → Execution moves to Dapr gRPC **proxy mode**

Not `InvokeMethodGrpcAsync` (deprecated API, see above) — **proxy mode**, the path the
deprecation notice itself points to: Execution hosts a real gRPC service; orchestration
calls it through `CreateInvocationInvoker`; the sidecars pass the gRPC call through
end-to-end.

### Contract

`src/BBT.Workflow.Execution.Abstractions/Protos/task_invoker.proto`:

```proto
syntax = "proto3";
package bbt.workflow.execution.v1;
option csharp_namespace = "BBT.Workflow.Execution.Grpc";

service TaskInvoker {
  rpc Invoke (InvokeRequest) returns (InvokeReply);
}

message InvokeRequest {
  string task_type = 1;
  string task_key = 2;
  // TaskInvokeRequest ({Envelope, TraceContext}) serialized with the SAME
  // System.Text.Json options the HTTP endpoint uses. JSON-in-bytes is deliberate:
  // Binding is a JsonElement and Result.Data is object — modelling them in proto
  // would change serialization semantics, which this migration must not do.
  bytes payload_json = 3;
}

message InvokeReply {
  // TaskInvokeResponse serialized the same way.
  bytes payload_json = 1;
}
```

### Both server surfaces stay alive; the sidecar annotation is the only switch

Kestrel serves HTTP/1.1 (controller, health probes, swagger) and h2c gRPC on the same
port (`Protocols: Http1AndHttp2`; gRPC clients use HTTP/2 prior knowledge). The HTTP
controller is **not** removed. Which delivery path works is decided solely by
`dapr.io/app-protocol` on the Execution pod:

- `http` (today) → HTTP-API invocation works, proxy mode doesn't.
- `grpc` (target) → proxy mode works, HTTP-API delivery to Execution breaks
  (sidecar would use AppCallback, which we don't implement).

Rollback is therefore a Helm value + one config flip — no code rebuild.

**Mixed-version constraint:** an old orchestration (HTTP client) cannot reach a
`grpc`-flipped Execution. Both ship in the same Helm release/chart, so the flip is
atomic per environment; the constraint is documented, not engineered around.

### Client

`RemoteInvokerService` gains a transport switch: `ExecutionApi:Transport` = `"http"`
(code default) | `"grpc"`. The gRPC path uses the generated `TaskInvoker.TaskInvokerClient`
over `DaprClient.CreateInvocationInvoker(appId, grpcChannelOptions: …)` with
`MaxReceiveMessageSize`/`MaxSendMessageSize` = 64 MB (aligned with the sidecar's
`http-max-request-size: "64"`). Deadline = the existing 60 s invocation budget (the
timeout hierarchy 60 ⊂ 300 ⊂ 330 is unchanged). Error mapping mirrors the HTTP path
exactly: `DeadlineExceeded` → the same 408 failure result, other `RpcException` → the
same 500 failure result, parent cancellation → rethrow. No transport retry, same as
today, same reason (side-effecting tasks).

Context propagation: `traceparent` flows as gRPC metadata (headers) automatically via
`Grpc.Net.Client` + the already-registered gRPC client instrumentation; the body-level
`TraceContext` fallback (`RestoreActivityFromBodyIfDetached`) keeps working because the
payload is byte-for-byte the same JSON. The 7 context headers the HTTP path adds are
sent as gRPC metadata with the same names (gRPC metadata *are* HTTP/2 headers, so
Execution's header-reading enrichers/middleware see the same keys).

### Non-goals

- No change to `DaprServiceTask` (Decision 1).
- No change to pub/sub, jobs, locks, state-store transports (user-deferred item).
- No removal of the HTTP invoke endpoint.
- No protobuf modelling of the task payload (JSON-in-bytes by design; revisit only with
  a measured serialization cost).

### Success criteria

1. A vnext-example flow runs green with `Transport=grpc` + `app-protocol: grpc` locally.
2. The trace shows: orchestration client span `bbt.workflow.execution.v1.TaskInvoker/Invoke`
   → sidecar `CallLocal/vnext-execution-app/...` → Execution server span, correctly parented.
3. Same flow still green after rolling both flips back to HTTP (rollback proof).
4. `dotnet build` clean; test-failure name set vs master baseline unchanged.
