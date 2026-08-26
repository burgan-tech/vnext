# Dapr Invocation Transport: What Uses gRPC, What Stays HTTP, and Why

## TL;DR

| Path | Transport | Why |
|---|---|---|
| Orchestration → Execution task invoke | gRPC proxy mode (opt-in per env) | Typed internal contract, single inbound surface, supported path |
| DaprServiceTask → external domain apps | HTTP API (unchanged) | gRPC→HTTP invocation is deprecated for removal; targets are HTTP apps |
| App → sidecar state/lock/pubsub calls | SDK-chosen (unchanged) | Deferred — evaluate after the above lands |

## The three hops (why "protocol: http" in Helm was never the knob)

The request "make `DaprServiceTask` always use gRPC" reads as if there is one dial to turn.
There isn't — a Dapr service invocation call crosses three separate hops, and only one of them
is configured by `dapr.io/app-protocol`:

1. **App → its own sidecar.** This is chosen by which SDK method the calling app invokes, not by
   any annotation. `DaprClient.InvokeMethodAsync`/`InvokeMethodWithResponseAsync` go over the
   SDK's plain HTTP client; `InvokeMethodGrpcAsync` and `CreateInvocationInvoker` (proxy mode) go
   over the SDK's gRPC channel. `DaprServiceTask` today calls the HTTP-shaped methods, which is
   the actual reason it runs over HTTP — not the Helm `protocol` value.
2. **Sidecar → sidecar.** Always gRPC. This hop is not configurable and does not read
   `app-protocol` at all — it is how the Dapr runtime talks to itself, unconditionally, on every
   service invocation regardless of what either app does.
3. **Sidecar → target app.** This is what `dapr.io/app-protocol` actually governs — per the Dapr
   reference, "the protocol Dapr uses to communicate with your app." It tells the *receiving*
   sidecar whether to hand the call to the local app over HTTP or gRPC (AppCallback). It has no
   effect on how the calling app reaches its own sidecar (hop 1) and no effect on hop 2.

So "protocol: http" in a Helm chart only ever controlled hop 3 for whichever app it was set on —
never the calling app's own outbound transport, and never the sidecar-to-sidecar leg. Flipping it
would not have moved `DaprServiceTask` to gRPC; only the SDK method the task calls does that, and
that method choice is what Decision 1 below turns on.

## DaprServiceTask: the evidence

All verified against primary sources on 2026-08-26.

- **Dapr v1.15 runtime, `pkg/api/grpc/grpc.go`, `InvokeService`** carries two deprecation notices
  in the shipped code, quoted verbatim:
  - `"InvokeService is deprecated and will be removed in the future, please use proxy mode instead."`
  - `"Invocation path of gRPC -> HTTP is deprecated and will be removed in the future."`

  The first flags the whole `InvokeService` gRPC API as deprecated. The second is more specific
  and more relevant here: the *gRPC caller → HTTP target* sub-path — exactly the shape "make
  `DaprServiceTask` always gRPC" would produce, since its targets are HTTP apps — has its own,
  separately announced removal.

- **Same function, HTTP-target semantics.** When the target is an HTTP app, a non-2xx response
  does not come back as a normal response with a status code attached. `ErrorFromHTTPResponseCode`
  turns it into a gRPC *error* (an `RpcException` on the caller side); the original HTTP status
  survives only as the `dapr-http-status` response header, and the response body of a failure
  rides inside the gRPC error message rather than as a distinguishable payload.

- **Dapr .NET SDK 1.17.9 (`DaprClientGrpc.cs`)** confirms the split at the client level:
  `InvokeMethodAsync`/`InvokeMethodWithResponseAsync` route through `httpClient.SendAsync` — the
  HTTP API, unaffected by the deprecation above. `InvokeMethodGrpcAsync` routes through
  `Client.InvokeServiceAsync` — the deprecated API. Separately,
  `DaprClient.CreateInvocationInvoker(appId, daprEndpoint, daprApiToken, grpcChannelOptions)`
  exists as the SDK's proxy-mode entry point — the thing the deprecation notice itself points
  callers toward, and the mechanism Decision 2 uses for Orchestration → Execution.

**Why this breaks `DaprServiceTask` specifically.** `DaprServiceTask`'s public contract is built
around free HTTP semantics: caller-chosen verb, query string, request/response headers,
`StatusCode`, `ReasonPhrase`, and `AcceptedStatusCodes` (a set of status codes a caller may declare
as "success" even when non-2xx). None of that survives the gRPC→HTTP path once the target
misbehaves: every non-2xx becomes an `RpcException` with the real status hidden inside a header on
an object most gRPC call sites never inspect, so `AcceptedStatusCodes` — the exact mechanism a task
author uses to say "404 is fine here" — cannot be evaluated the same way, and the resulting
contract silently degrades between "worked," "the target said no," and "the transport broke."

**Verdict: not implementable in a supported form today.** Running `DaprServiceTask` over gRPC
means running it over an API Dapr has already deprecated (`InvokeService`) and, more specifically,
over the exact sub-path (gRPC caller → HTTP target) whose removal has already been announced
separately. This document is that decision record — the question is answered once, with the
runtime-source evidence inline, so it does not need to be re-litigated from scratch the next time
it comes up.

**Re-open condition.** Revisit only if/when `DaprServiceTask`'s targets themselves start speaking
gRPC. At that point per-target proxy mode — the same `CreateInvocationInvoker` mechanism Decision 2
below builds for Orchestration → Execution — is the road back in, and this work's machinery is
exactly what it would reuse. Nothing about today's HTTP-target contract changes until then.

## Orchestration → Execution: the design

Execution's Dapr-inbound surface is a single endpoint
(`POST api/v{version}/execution/invoke/{type}/{key}`, `ExecutionController`) with no `[Topic]`
subscriptions, no job callbacks, and no other inbound service invocation — a narrow, typed, fully
internal contract, unlike `DaprServiceTask`'s open-ended external HTTP targets. That difference is
what makes gRPC viable here in a way it is not for `DaprServiceTask`: proxy mode, not
`InvokeMethodGrpcAsync`, is the path the deprecation notice itself points to — Execution hosts a
real gRPC service, Orchestration's `RemoteInvokerService` calls it through
`DaprClient.CreateInvocationInvoker`, and the sidecars pass the gRPC call through end-to-end
instead of degrading it to HTTP at any hop.

Both server surfaces stay alive on Execution: Kestrel serves HTTP/1.1 (the existing controller,
health probes, swagger) and h2c gRPC on the same port. Which delivery path actually works is
decided solely by `dapr.io/app-protocol` on the Execution pod — `http` today (HTTP-API invocation
works, proxy mode doesn't), `grpc` as the target state (proxy mode works, HTTP-API delivery to
Execution breaks because the sidecar would route to an AppCallback that isn't implemented). Because
the switch is a Helm value rather than a code path, rollback is a config flip and not a rebuild —
with the caveat that an old, HTTP-only Orchestration cannot reach a `grpc`-flipped Execution, so
both ship in the same Helm release and the flip is atomic per environment, not engineered around.

Today's wire contract, unchanged by this design, is plain JSON: the request is
`TaskInvokeRequest { Envelope, TraceContext }`, the response is
`TaskInvokeResponse { Success, ErrorMessage, Result, ExecutionDurationMs }`. The payload stays
JSON-in-bytes under proxy mode rather than being modelled in protobuf — `TaskEnvelope.Binding` is a
`JsonElement` and `TaskInvokeResponse.Data` is `object?`, and re-modelling either in proto would
change serialization semantics, which this migration is explicitly not allowed to do.

Orchestration's current client for this hop, `RemoteInvokerService`, is configured with
`ExecutionApi:AppId` and `ExecutionApi:InvocationTimeoutSeconds = 60`, and it performs no
transport retry by design: the sidecar resiliency policy for this call is circuit-breaker-only,
because retrying would re-invoke tasks that can have side effects — a decision recorded in the
class's own remarks, not an oversight. Proxy mode does not touch either of these: the same app-id,
the same 60-second budget, and the same no-retry policy carry over to the gRPC path unchanged.

Full design (proto contract, `RemoteInvokerService`'s transport switch, error mapping, context
propagation, and success criteria): see
[`docs/superpowers/specs/2026-08-26-execution-grpc-transport-spec.md`](../superpowers/specs/2026-08-26-execution-grpc-transport-spec.md).

## Verification

Performed 2026-08-26 against a locally built runtime (`dotnet run --launch-profile http`, all
four apps) with `vnext-execution-dapr` recreated with `--app-protocol grpc` and orchestration's
`ExecutionApi:Transport` set to `grpc`. MoneyTransfer integration test suite
(`vnext-example/tests/Core.IntegrationTests`, filter `MoneyTransferTests`) run against
`localhost:4201`, traces read from Elastic (`localhost:9200`, `.ds-traces-apm*`).

### What verified

**The orchestration-side gRPC client hop is real and wired correctly.** In the post-flip trace
(`f221ac72916add2600e156cec6102935`), `Task.Invoke` (span `779f3f9d7a65a66a`, `BBT.Workflow.Tasks`)
has a child span `bbt.workflow.execution.v1.TaskInvoker/Invoke`
(`OpenTelemetry.Instrumentation.GrpcNetClient`) exactly where the old
`Dapr invoke vnext-execution-app` / `CallLocal/vnext-execution-app/api/v1/execution/invoke/http/...`
HttpClient chain used to sit in the pre-flip baseline trace
(`6b85c250d727e7bdd98a620f0e019563`). Underneath it sits a `POST` span (`System.Net.Http`,
`localhost:42111`, HTTP 200) — the actual HTTP/2 frame to the orchestration sidecar's gRPC port —
confirming `RemoteInvokerService` is genuinely dialing gRPC via `DaprClient.CreateInvocationInvoker`
when `Transport: grpc` is set, not silently falling back to HTTP. This part of Task 2–5's work is
proven correct.

Span tree observed (post-flip, orchestration side, trimmed to the invocation branch):

```
Task.Execute.execute-transfer (BBT.Aether.Aspects)
└─ Task.Invoke (BBT.Workflow.Tasks)
   └─ bbt.workflow.execution.v1.TaskInvoker/Invoke (OpenTelemetry.Instrumentation.GrpcNetClient) [outcome=failure]
      └─ POST (System.Net.Http, localhost:42111, HTTP 200)  ← reaches orchestration's own sidecar fine
         └─ /bbt.workflow.execution.v1.TaskInvoker/Invoke (dapr-diagnostics, service=vnext-app) [outcome=failure]
            └─ /bbt.workflow.execution.v1.TaskInvoker/Invoke (dapr-diagnostics, service=vnext-execution-app) [outcome=failure, duration=106µs, NO CHILDREN]
```

### What did NOT verify — a real, reproduced gap, not worked around

**The call never reaches Execution's application code.** The expected shape (per the task brief)
is Execution's server transaction being the gRPC method with `Invoke.{taskType}/{taskKey}` spans
hanging beneath it — mirroring the pre-flip baseline's
`POST api/v{version}/execution/invoke/{type}/{key}` → `Invoke.http/execute-transfer` → outbound
`POST` chain. Instead, the Execution-side transaction
(`/bbt.workflow.execution.v1.TaskInvoker/Invoke`, `service.name=vnext-execution-app`,
`framework=dapr-diagnostics`) has **zero children** and completes in **106µs** — far too fast to
have reached the task invoker, DB, or the outbound HTTP call the HTTP-transport path makes at this
same point. No `Microsoft.AspNetCore`/`Grpc.AspNetCore` server transaction and no
`Invoke.http/execute-transfer` span ever appear anywhere in this trace.

Root cause, confirmed directly (not inferred from the trace alone):

- Execution's own log (`execution.log`) shows, at startup:
  ```
  warn: Microsoft.AspNetCore.Server.Kestrel[64]
        HTTP/2 is not enabled for 127.0.0.1:4202. The endpoint is configured to use HTTP/1.1 and
        HTTP/2, but TLS is not enabled. HTTP/2 requires TLS application protocol negotiation.
        Connections to this endpoint will use HTTP/1.1.
  ```
  (Event `Http2DisabledWithHttp1AndNoTls`, logged for both `127.0.0.1:4202` and `[::1]:4202`.)
- Reproduced independently of any Dapr/Aether code: `curl --http2-prior-knowledge http://localhost:4202/health`
  fails with *"Remote peer returned unexpected data while we expected SETTINGS frame. Perhaps, peer
  does not support HTTP/2 properly."* — i.e. that endpoint genuinely serves HTTP/1.1 only.
- Cause: `appsettings.json`'s `Kestrel:EndpointDefaults:Protocols = Http1AndHttp2` (added in Task 4)
  only applies to endpoints declared under `Kestrel:Endpoints`. When the app is started the way this
  repo's own local-dev instructions require — `dotnet run --launch-profile http`, which supplies
  `applicationUrl: http://localhost:4202` from `launchSettings.json` (i.e. via `ASPNETCORE_URLS`,
  not `Kestrel:Endpoints`) — Kestrel synthesizes the listening endpoint straight from that URL and
  does **not** apply `EndpointDefaults` to it. The endpoint silently downgrades to HTTP/1.1-only,
  confirmed by the warning above. `host.orb.local` (the sidecar's `--app-channel-address`) reaches
  this same loopback-bound endpoint — the same one the HTTP-transport path uses successfully — so
  the sidecar's h2c dial lands on a socket that cannot speak HTTP/2, and the proxy-mode call fails
  at the transport layer before any application code runs. `RemoteInvokerService`'s gRPC error
  mapping (by design, see spec) turns that transport failure into a task-level failure result
  (`transferResult: {"success":false,"statusCode":500}`), which the workflow then legitimately
  fault-transitions on — the instance's own behavior is correct; the failure is upstream of it.

  This is a **local-dev-only gap** in how Task 4's Kestrel config was validated (the design doc
  states "Kestrel serves HTTP/1.1 ... and h2c gRPC on the same port," which is the intent, but that
  was evidently never checked against the `--launch-profile http` startup path this task's own
  instructions mandate). It has not been patched here — per this task's explicit instructions, an
  unmet expectation here is a finding to report, not a workaround to write. A fix belongs in Task 4
  scope (e.g. an explicit `Kestrel:Endpoints:Http:Url` config entry, or `ConfigureEndpointDefaults`
  called in code) and should be tracked separately.

**Test evidence.** `MoneyTransferTests`, gRPC transport flipped: `Failed! Failed: 2, Passed: 3,
Total: 5` — both failures (`HappyPath_ReachesTransferCompleted`,
`ExecutingTransfer_RecordsTheProvidersResultInInstanceData`) are exactly the two tests that reach
the `execute-transfer` DaprService task; the three tests that never reach that task
(`SubmitDetails_RejectsAPayloadThatViolatesTheTransitionSchema`, `AwaitingPushApproval_ArmsTheTimeoutTimer`,
`Cancel_MovesTheTransferToCancelled`) passed. This is the expected fingerprint of a transport-layer
failure isolated to the gRPC hop, not test flakiness — reinforced by the fact the same suite passed
`5/5` immediately before the flip (HTTP) and immediately after the rollback (HTTP again, see below).

**Orphan-span comparison (Step 3's other assertion).** Pre-flip HTTP baseline trace
(`6b85c250d727e7bdd98a620f0e019563`, 118 docs): 3 orphan spans, all `POST`/`System.Net.Http`
external spans whose `parent.id` does not resolve within the trace (pre-existing — deferred
pub/sub-publish spans, unrelated to transport). Post-flip trace
(`f221ac72916add2600e156cec6102935`, 83 docs): 2 orphan spans, same shape. No new orphan pattern was
introduced by the transport flip; the lower absolute count in the second trace simply reflects the
shorter pipeline run (it aborted at the failed task instead of completing the flow).

### Rollback proof (Step 4)

Reverted both switches in the running environment — `ExecutionApi:Transport: "http"`,
`vnext-execution-dapr` recreated without `--app-protocol` (defaults to `http`) — rebuilt and
restarted orchestration, reran the identical filter:

```
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 5 s
```

Confirms the HTTP path is fully intact and the rollback is config-only in both directions: the same
orchestration binary, same Execution binary, unchanged — only `ExecutionApi:Transport` and the
sidecar's `--app-protocol` flag moved. Both switches were then restored to `grpc` (the committed
local-dev state) and orchestration restarted once more; `/health` returns 200 in that final state.

### Bottom line

Config flip mechanics (Task 6's own deliverable) work as designed and are proven safe to flip in
both directions. The gRPC client path (Task 2/3's `RemoteInvokerService` + proxy-mode dial) is
proven real, not a silent HTTP fallback. What is **not** proven is a successful end-to-end gRPC task
invocation in this local-dev environment — it is blocked by the Kestrel binding gap above, which
predates this task and sits in Task 4's Program.cs/appsettings.json, not in the two files this task
touched. Anyone relying on "gRPC works locally" from this repo state should not assume that until
that gap is closed and this test is rerun.
