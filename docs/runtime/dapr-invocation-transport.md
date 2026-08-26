# Dapr Invocation Transport: What Uses gRPC, What Stays HTTP, and Why

## TL;DR

| Path | Transport | Why |
|---|---|---|
| Orchestration → Execution task invoke | gRPC proxy mode (opt-in per env) | Typed internal contract, single inbound surface, supported path |
| DaprServiceTask → external domain apps | HTTP API (unchanged) | gRPC→HTTP invocation is deprecated for removal; targets are HTTP apps |
| App → sidecar state/lock/pubsub calls | SDK-chosen (unchanged) | Deferred — evaluate after the above lands |

> **Known limitation, confirmed with evidence:** gRPC proxy mode is business-correct
> (`MoneyTransferTests` `5/5`) but produces **two disconnected traces per task invocation**, not
> one — root-caused to Dapr's AppCallback hop delivering a duplicated, W3C-invalid `traceparent` to
> the app, external to this codebase. See "KNOWN LIMITATION" under Verification below before
> enabling gRPC anywhere trace-tree wholeness matters.

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

### KNOWN LIMITATION — gRPC proxy mode produces TWO disconnected traces per task invocation

**Confirmed with direct evidence, root-caused to a specific mechanism, and not fixable from this
codebase.** Every task invocation over gRPC proxy mode yields two separate top-level traces where
the HTTP path produces one continuous trace. This is a real, verified regression in trace-tree
integrity — the entire reason this branch (`feature/trace-span-tree`) exists — and is documented
here prominently, not buried, so anyone deciding whether to enable gRPC locally or in an environment
understands the trade explicitly.

**The shape.** Orchestration's own trace (e.g. `93ddf309ef6bcad9201cf60f4cec3bdb`) contains the
gRPC client span (`bbt.workflow.execution.v1.TaskInvoker/Invoke`, `GrpcNetClient` instrumentation,
correctly parented under `Task.Invoke`) and the two sidecar-side `dapr-diagnostics` spans (caller's
own sidecar, then the callee's sidecar) — and **no `Microsoft.AspNetCore` transaction at all**.
Execution's real work — `Microsoft.AspNetCore POST /bbt.workflow.execution.v1.TaskInvoker/Invoke`
→ `Invoke.http/execute-transfer` → the outbound provider call — lands in a **separate, freshly-rooted
trace** (e.g. `0f21fe1b6eb06dfe3a6d4dc5bed24a1b`, `parent: None`), with `user_agent: grpc-go/1.73.0`
confirming the request that reached Execution's ASP.NET Core pipeline came from the Dapr sidecar's
own Go gRPC client, not a raw proxy of the original stream. Timestamps (~40ms apart) and durations
match — this is one logical call, split into two traces, not two different calls. Repeats
identically on every `execute-transfer` invocation observed, both before and after the fix below.

**Root cause, confirmed empirically, not guessed — three things were tested in order:**

1. *Hypothesis: the caller never sends `traceparent` on the wire.* Disproven directly. A temporary
   diagnostic on Execution's gRPC service (`context.RequestHeaders`) showed `traceparent` present
   on **every** call — .NET's `HttpClient` `DiagnosticsHandler` (which `Grpc.Net.Client`'s channel
   runs on) auto-injects it from `Activity.Current` on every outbound gRPC call, framework-level,
   independent of any OTel package. It was never missing.
2. *Hypothesis: sending it explicitly (from `TaskTraceContext.TraceParent`/`TraceState`, already
   populated from `Activity.Current` in `RemoteInvokerService.CreateTraceContext`) fixes it.*
   Tested and disproven. Adding an explicit `metadata.Add("traceparent", ...)` in
   `InvokeOverGrpcAsync` did not help, because of finding 3 below — it only adds a second value to
   an already-broken header.
3. **The actual defect: `traceparent` arrives duplicated on every call, regardless of whether this
   codebase sends it explicitly.** The raw value Execution receives looks like:
   ```
   traceparent = 00-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01,00-1c00cbe047937c981316a9a85f69bad6-5c6c3f4a64d19975-01
   ```
   Same trace id, two different span ids, comma-joined into one value. This happens upstream of
   anything this codebase controls — on the Dapr sidecar's app-bound (AppCallback) hop, which
   re-issues its own gRPC call to the app rather than proxying the original HTTP/2 stream, and
   evidently stamps its own span alongside forwarding the original rather than replacing it. A
   value with more than one `traceparent` is **invalid per the W3C Trace Context spec** — a
   compliant receiver MUST treat it as if no trace context was present — which is exactly what
   ASP.NET Core's built-in hosting instrumentation does: it starts a fresh root `Activity`, becoming
   the disconnected trace above. This is Dapr's own AppCallback behavior, external to this
   repository, and not something `RemoteInvokerService` or the Execution gRPC service can correct
   by sending headers differently — per the decision this investigation was scoped to, this means
   **stop trying to force it at the transport level.**

**What already works, and its real limit.** `TaskInvokeHandler.HandleAsync` calls
`RestoreActivityFromBodyIfDetached(traceContext)`, using the trace context carried in the *request
body* (`TaskTraceContext.TraceParent`/`TraceState` — a clean, single, valid value, captured from
`Activity.Current` on the orchestration side before it ever touches the wire) as a fallback,
independent of the broken wire header. Confirmed working, live, in Elastic: the disconnected trace's
`Microsoft.AspNetCore` transaction carries `labels.vnext_trace_mismatch: "true"` and
`span.links: [{trace.id: "<caller's trace>", span.id: "<caller's span>"}]` — proof the fallback ran
and correctly linked the two traces. **But it cannot merge them into one tree**, and the reason is
structural, not a bug in that code: `Activity.ParentId` is fixed at `Activity.Start()` and cannot be
changed afterward, and ASP.NET Core's hosting layer has **already started** the `Microsoft.AspNetCore`
transaction's `Activity` — reading (and discarding, per point 3) the malformed incoming
`traceparent` — before `TaskInvokeHandler`'s code ever runs. By the time the fallback executes,
`Activity.Current` is a non-null, already-rooted activity; the branch that could parent a *new*
activity onto the caller's context (`ambient is null`) is dead code on this path. The only thing
left to do at that point is exactly what the code already does: link, don't re-parent.

**Net effect: the two traces are navigable from each other** (via the `span.links` entry, visible
in Elastic/APM UIs that render trace links) **but remain two separate trace trees**, not one. This
does not affect business correctness — `MoneyTransferTests` passes `5/5` — but it is a real,
verified gap against this branch's own goal of a single, whole trace tree per request, and it is
external to this codebase's ability to fix without either (a) Dapr changing its AppCallback
trace-context forwarding behavior, or (b) a workaround this investigation did not find and was
explicitly told to stop searching for once the root cause was confirmed external.

**Orphan-span comparison**, now on the caller's successful trace: pre-flip HTTP baseline
(`6b85c250d727e7bdd98a620f0e019563`, 118 docs): 3 orphan spans (pre-existing, deferred
pub/sub-publish `POST` spans, unrelated to transport). Post-fix gRPC trace
(`93ddf309ef6bcad9201cf60f4cec3bdb`, 114 docs): 3 orphan spans, same shape — no new *in-trace*
orphan pattern. The real regression is not an in-trace orphan; it is that Execution's entire real
span subtree is missing from the caller's trace altogether, landing in a second trace instead — the
`span.links` connection is the only bridge between them.

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

Config flip mechanics work as designed and are proven safe to flip in both directions, three times
now. The gRPC client path is proven real. The single-port Kestrel design in the original spec was
proven unworkable and corrected to two ports, with that fix itself verified independently (both
ports probed directly) before being verified through the test suite (`5/5`). Business-level
end-to-end correctness over gRPC is now proven: `MoneyTransferTests` passes, Execution's real
task-invoker work happens, with correct data, on the correct port, via the correct protocol.

**Distributed trace continuity across the gRPC hop is disproven, with a confirmed, external root
cause, not an open question.** Every task invocation over gRPC yields two disconnected traces
instead of one (see the KNOWN LIMITATION section above) — root-caused to Dapr's AppCallback hop
delivering a duplicated, W3C-invalid `traceparent` value to the app regardless of what this
codebase sends, which is outside this repository's ability to fix. The existing
`ActivityLink`/`span.links` fallback already connects the two traces for manual navigation but
cannot merge them into one tree, for a structural reason (`Activity.ParentId` is immutable once
ASP.NET Core's hosting layer has started the request's `Activity`, which happens before any of this
codebase's own code runs). This does not block business functionality but is a real, verified gap
against this branch's own goal of a whole trace tree per request — visible here explicitly rather
than left implicit, so it factors into the decision to enable gRPC in any environment.

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
