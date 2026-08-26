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

Both server surfaces stay alive on Execution, but **on two separate cleartext ports, not one**.
The original design assumed a single `Protocols: Http1AndHttp2` Kestrel endpoint could serve both;
Task 6 verification proved that unworkable and it was corrected in the same task. The constraint:
**without TLS there is no ALPN to negotiate HTTP/1.1 vs HTTP/2, and Kestrel does not byte-sniff the
client preface to multiplex both on one cleartext port — it just downgrades the whole endpoint to
HTTP/1.1.** (Confirmed directly: `curl --http2-prior-knowledge` against a cleartext
`Http1AndHttp2`-configured endpoint fails with *"Remote peer returned unexpected data while we
expected SETTINGS frame"*, and Kestrel itself logs `Http2DisabledWithHttp1AndNoTls` at startup —
*"The endpoint **is** configured to use HTTP/1.1 and HTTP/2 ... Connections to this endpoint will
use HTTP/1.1."* The config is honored; the protocol simply cannot be negotiated without TLS.)

So Execution's `Program.cs` opens two explicit `ListenAnyIP` endpoints: an HTTP/1.1-only port
(`Kestrel:HttpPort`, default `4202` — the existing controller, health probes, swagger) and an
Http2-only h2c port (`Kestrel:GrpcPort`, default `4212` — the gRPC `TaskInvoker` service). Both
keys are plain configuration (overridable via env vars, e.g. `Kestrel__GrpcPort`, or
`appsettings.{Environment}.json`), not hardcoded in code. A comment at the Kestrel configuration
site in `Program.cs` quotes the constraint so it is not "simplified" back to one port.

Which delivery path actually works is decided by **two things that must move together**:
`dapr.io/app-protocol` on the Execution pod (`http` → HTTP-API invocation works, proxy mode
doesn't; `grpc` → proxy mode works, HTTP-API delivery breaks because the sidecar routes to an
AppCallback that isn't implemented there) **and** `dapr.io/app-port`, which must point at whichever
port actually speaks that protocol (`4202` for `http`, `4212` for `grpc` in this app's default
config). Setting only one of the two breaks the pod: `app-protocol: grpc` with `app-port: 4202`
dials h2c against an HTTP/1.1-only socket and fails at the transport layer before any application
code runs (this exact failure was reproduced and is documented in Verification below); the
reverse — `app-protocol: http` with `app-port: 4212` — fails the sidecar's own TCP-listen probe
against an endpoint that never speaks HTTP/1.1. Because the switch is a Helm value rather than a
code path, rollback is a config flip and not a rebuild — with the caveat that an old, HTTP-only
Orchestration cannot reach a `grpc`-flipped Execution, so both ship in the same Helm release and
the flip is atomic per environment, not engineered around. **Task 7 (Helm) must carry both
annotations in lockstep, plus expose the Execution container's second port** (`4212` by default) —
setting `app-protocol` without also moving `app-port` and opening the container port is the failure
mode most likely to be reintroduced there.

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

Performed 2026-08-26 against a locally built runtime (`dotnet run --launch-profile http`, all four
apps) with `vnext-execution-dapr` recreated per state below, traces read from Elastic
(`localhost:9200`, `.ds-traces-apm*`), MoneyTransfer integration test suite
(`vnext-example/tests/Core.IntegrationTests`, filter `MoneyTransferTests`) run against
`localhost:4201`. This section covers two passes: an initial flip that surfaced a real defect, and
the corrected two-port design that fixed it.

### Pass 1 — single-port `Http1AndHttp2`, as originally designed: did not verify

With `ExecutionApi:Transport: grpc` and `--app-protocol grpc` (`--app-port 4202`, one Kestrel
endpoint), `MoneyTransferTests` gave `Failed: 2, Passed: 3` — not `5/5`. The orchestration-side
gRPC client span (`bbt.workflow.execution.v1.TaskInvoker/Invoke`, `GrpcNetClient` instrumentation)
was present and correctly parented under `Task.Invoke`, proving the client hop was real and not a
silent HTTP fallback. But the call never reached Execution's application code: the Execution-side
transaction had zero children and completed in 106µs. Root cause, confirmed directly: Kestrel's
`Http2DisabledWithHttp1AndNoTls` warning at startup, and independently reproduced with
`curl --http2-prior-knowledge http://localhost:4202/health` failing with *"Remote peer returned
unexpected data while we expected SETTINGS frame."* A cleartext `Http1AndHttp2` endpoint cannot
actually serve both protocols — see the constraint explained above under Decision 2.

This was corrected in the same task (Program.cs / appsettings.json now split into two ports, see
above) rather than left as an open gap, once the precise mechanism was confirmed.

### Pass 2 — two ports (4202 HTTP/1.1, 4212 h2c gRPC): verified end-to-end for business correctness

With the fix in place — `Kestrel:HttpPort=4202` (Http1 only), `Kestrel:GrpcPort=4212` (Http2 only),
sidecar `--app-port 4212` + `--app-protocol grpc` — startup no longer logs any Kestrel HTTP/2
warning. Direct, independent confirmation of both ports before running any test:

```
$ curl -s -o /dev/null -w "%{http_version} %{http_code}\n" http://localhost:4202/health
1.1 200
$ curl --http2-prior-knowledge http://localhost:4202/health   # correctly rejected — HTTP/1.1-only now
Remote peer returned unexpected data while we expected SETTINGS frame.
$ curl --http2-prior-knowledge http://localhost:4212/         # succeeds — real ASP.NET Core pipeline
HTTP/2 404  (x-trace-id, x-span-id, traceparent headers present — reached the app, not just Kestrel)
$ curl -s -o /dev/null -w "%{http_version} %{http_code}\n" http://localhost:4212/health  # correctly rejected — Http2-only now
1.1 400
```

Sidecar log confirms it dialed the right port for the right protocol:
```
application protocol: grpc. waiting on port 4212.
application discovered on port 4212
```

`MoneyTransferTests`: **`Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 6 s`**
(2026-08-26T14:08:16Z). Business result is correct end-to-end over gRPC.

**Execution's server-side span tree is confirmed to exist and be correctly shaped.** For the
successful `execute-transfer` invocation, Elastic holds:

```
Microsoft.AspNetCore  POST /bbt.workflow.execution.v1.TaskInvoker/Invoke     [transaction, id 66b837b9446e02bd, parent=None]
└─ Invoke.http/execute-transfer (BBT.Workflow.Execution.Invokers)             [span]
   └─ POST (System.Net.Http)  ← outbound call to the provider (MockLab)       [span]
```

This is exactly the shape Step 3 of the original brief asked for — the server transaction is the
gRPC method, not `POST api/v{version}/execution/invoke/{type}/{key}`, and `Invoke.{taskType}/{taskKey}`
hangs beneath it.

### What did NOT verify — trace continuity across the sidecar→app gRPC hop

**`parent=None` on that transaction is the tell.** It is not nested under the caller's trace. The
orchestration side's view of the same call is a *different* trace id entirely:

```
trace 93ddf309ef6bcad9201cf60f4cec3bdb (orchestration side, caller's trace)
Task.Execute.execute-transfer (BBT.Aether.Aspects)
└─ Task.Invoke (BBT.Workflow.Tasks)
   └─ bbt.workflow.execution.v1.TaskInvoker/Invoke (GrpcNetClient client span)   [success, 347ms]
      └─ /bbt.workflow.execution.v1.TaskInvoker/Invoke (dapr-diagnostics, svc=vnext-app)
         └─ /bbt.workflow.execution.v1.TaskInvoker/Invoke (dapr-diagnostics, svc=vnext-execution-app)
                                                          [success, 345ms, NO CHILDREN IN THIS TRACE]

trace 0f21fe1b6eb06dfe3a6d4dc5bed24a1b (Execution's real work — a DIFFERENT, freshly-rooted trace)
Microsoft.AspNetCore POST /bbt.workflow.execution.v1.TaskInvoker/Invoke  [parent=None]
└─ Invoke.http/execute-transfer (BBT.Workflow.Execution.Invokers)
   └─ POST (System.Net.Http)
```

The two traces' timestamps are ~40ms apart and their durations match (345ms vs the real work
completing moments later) — this is the *same underlying call*, split across two disconnected
traces, not two different calls. This pattern repeated identically for the second successful
`execute-transfer` invocation in the same test run (trace pair `d27bda1690ca7408.../6a0fc1fe87d2...`).

This did not happen under HTTP transport: the pre-flip baseline trace
(`6b85c250d727e7bdd98a620f0e019563`) carried the entire request — orchestration, sidecar hop,
Execution's HTTP route transaction, and `Invoke.http/execute-transfer` — as one continuous trace.
Under gRPC, W3C trace context is not being carried across the Dapr sidecar → Execution app hop into
the ASP.NET Core-hosted gRPC service's own `Activity` (the sidecar-to-sidecar hop, and orchestration
→ its own sidecar, both propagate correctly — only the last hop, sidecar → app AppCallback, drops
it). This is a genuine, reproduced gap in context propagation specific to gRPC proxy-mode delivery,
separate from the Kestrel binding gap Pass 1 found and distinct from anything Task 6's own file
scope (`docker-compose.yml`, orchestration `appsettings.json`) can fix — it lives in how
`RemoteInvokerService`/the Execution gRPC service handle `TaskTraceContext` versus the transport-level
W3C headers, which the design doc calls out as its own concern ("context propagation") but which
was not exercised end-to-end before this task. Not fixed here — reporting it, not working around it,
per the same principle as Pass 1's finding.

**Orphan-span comparison**, now on the successful trace: pre-flip HTTP baseline
(`6b85c250d727e7bdd98a620f0e019563`, 118 docs): 3 orphan spans (pre-existing, deferred
pub/sub-publish `POST` spans, unrelated to transport). Post-fix gRPC trace
(`93ddf309ef6bcad9201cf60f4cec3bdb`, 114 docs): 3 orphan spans, same shape — no new orphan pattern
introduced *within* a trace. The real regression is not an in-trace orphan; it is that Execution's
entire real span subtree is missing from the caller's trace altogether (it is a full second trace,
not an orphan span within the first).

### Rollback proof (Step 4), re-run after the fix

The fix changed which files and ports are load-bearing, so the rollback was re-verified rather than
assumed still valid. Reverted, in the running environment only: `ExecutionApi:Transport: "http"`,
sidecar `--app-port` back to `4202`, `--app-protocol` removed. Recreated sidecar, rebuilt +
restarted orchestration, reran the identical filter:

```
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 5 s
```
(2026-08-26T14:13:25Z)

Both switches were then restored to the committed gRPC state (`Transport: grpc`,
`--app-port 4212` + `--app-protocol grpc`), sidecar recreated, orchestration rebuilt and restarted;
`/health` returns 200 in that final state. Execution's dual-port Kestrel configuration does not need
to change for rollback — both ports stay open regardless of which transport Orchestration is
configured to use; only Orchestration's `Transport` setting and the sidecar's two flags move.

### Bottom line

Config flip mechanics work as designed and are proven safe to flip in both directions, twice now.
The gRPC client path is proven real. The single-port Kestrel design in the original spec was
proven unworkable and corrected to two ports, with that fix itself verified independently (both
ports probed directly) before being verified through the test suite (`5/5`). Business-level
end-to-end correctness over gRPC is now proven: `MoneyTransferTests` passes, Execution's real
task-invoker work happens, with correct data, on the correct port, via the correct protocol.
**What remains unproven is distributed trace continuity across the gRPC hop** — Execution's
server-side span tree is real and correctly shaped, but lands in a disconnected trace rather than
the caller's, a regression from the HTTP path's single unified trace. This should be tracked and
fixed as a follow-up (see the context-propagation note above); it does not block the business
functionality but does undermine the tracing story this branch (`feature/trace-span-tree`) exists
to build.

### For Task 7 (Helm)

A deployment must set, together, never independently:
- `dapr.io/app-protocol: "grpc"` on the Execution pod annotation.
- `dapr.io/app-port` pointed at Execution's **gRPC** container port (`4212` by default here,
  configurable via `Kestrel:GrpcPort` / `Kestrel__GrpcPort`), not its HTTP port.
- The container must expose **both** ports — `4202` (HTTP/1.1: controller, health probes, swagger)
  stays needed regardless of transport (health probes, direct debugging), and `4212` (h2c gRPC) is
  the new one Task 7 must add to the container's port list / service definition.

Setting `app-protocol: grpc` without moving `app-port` to `4212` reproduces Pass 1's failure exactly
(h2c dial against an HTTP/1.1-only port, near-instant transport-layer failure, task result silently
becomes `{success:false, statusCode:500}`). Setting `app-port: 4212` without `app-protocol: grpc`
breaks the sidecar's own startup TCP-listen probe against a port that never speaks HTTP/1.1. Both
values are plain configuration on the Execution side (`Kestrel:HttpPort`/`Kestrel:GrpcPort` in
`appsettings.json`, overridable per-environment), not hardcoded, so Helm can also relocate the ports
themselves if `4202`/`4212` collide with something else in a given cluster — as long as the two
Dapr annotations are updated to match.
