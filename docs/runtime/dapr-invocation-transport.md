# Dapr Invocation Transport: What Uses gRPC, What Stays HTTP, and Why

## TL;DR

| Path | Transport | Why |
|---|---|---|
| Orchestration → Execution task invoke | gRPC proxy mode (opt-in per env) | Typed internal contract, single inbound surface, supported path |
| DaprServiceTask → external domain apps | HTTP API (unchanged) | gRPC→HTTP invocation is deprecated for removal; targets are HTTP apps |
| App → sidecar state/lock/pubsub calls | SDK-chosen (unchanged) | Deferred — evaluate after the above lands |
| Cross-domain `Remote*` services (orchestrator → other domain's orchestrator) | `DaprClient.CreateInvokeHttpClient()` (SDK `InvocationHandler`, HTTP to the sidecar), opt-in via `ServiceDiscovery:Provider=dapr` | The whole `DaprClient.InvokeMethod*` family is `[Obsolete]` in 1.17; `CreateInvokeHttpClient` is the surface its message points at. Targets are HTTP/JSON controllers, so gRPC on the first hop is not available. See "Cross-domain Remote* services" below. |

> **Formerly a known limitation, now fixed (2026-08-26):** gRPC proxy mode used to produce **two
> disconnected traces per task invocation** — root-caused to Dapr's AppCallback hop delivering a
> duplicated, W3C-invalid `traceparent` to the app. Dapr still sends that malformed header, but the
> Execution host now tolerates it via `DuplicateTolerantTraceContextPropagator`, and gRPC
> invocations produce **one whole trace tree**, verified live. Business correctness unchanged
> (`MoneyTransferTests` `5/5` on both transports). See "RESOLVED (was: KNOWN LIMITATION)" under
> Verification for the evidence and the reasoning behind the fix's placement.

## The three hops (why "protocol: http" in Helm was never the knob)

The request "make `DaprServiceTask` always use gRPC" reads as if there is one dial to turn.
There isn't — a Dapr service invocation call crosses three separate hops, and only one of them
is configured by `dapr.io/app-protocol`:

1. **App → its own sidecar.** This is chosen by which SDK method the calling app invokes, not by
   any annotation. `DaprClient.CreateInvokeHttpClient()` (and the now-`[Obsolete]`
   `InvokeMethodAsync`/`InvokeMethodWithResponseAsync`) go over the SDK's plain HTTP client;
   `InvokeMethodGrpcAsync` (also obsolete) and `CreateInvocationInvoker` (proxy mode) go over the
   SDK's gRPC channel. `DaprServiceTask` sends through `DaprServiceInvocationClient`, i.e. the
   HTTP invocation client, which is the actual reason it runs over HTTP — not the Helm `protocol`
   value.
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
(the existing controller, health probes, swagger) and an Http2-only h2c port (`Kestrel:GrpcPort`,
default `4212` — the gRPC `TaskInvoker` service). The HTTP/1.1 port is deliberately **not** a
second bespoke key — registering any code-based `Listen*` endpoint makes Kestrel discard the
hosting URLs wholesale, so `Program.cs` parses the port back out of the hosting-URL configuration
(`ASPNETCORE_URLS`/`--urls`) instead, keeping that variable — the platform's long-standing way of
declaring this port, set by `vnext.commonEnvVars` in Helm and by `applicationUrl` in
launchSettings — the single source of truth (default `4202` only when no hosting URL is
configured at all). `Kestrel:GrpcPort` stays a real, standalone key because the app's gRPC listen
port is new information with no pre-existing environment variable — it is not, and cannot be,
`DAPR_GRPC_PORT`: that is the *sidecar's* own API port, carried identically to every service by
`vnext.commonEnvVars`, which the app calls *out* to Dapr on; an app listening there would collide
with its own sidecar. `Kestrel:GrpcPort` is plain configuration (overridable via env vars, e.g.
`Kestrel__GrpcPort`, or `appsettings.{Environment}.json`), not hardcoded in code. A comment at the Kestrel configuration
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

## Cross-domain Remote* services: DaprClient, HTTP to the sidecar, and why not gRPC (2026-09)

The `Remote*` app services (`RemoteInstance{Command,Query,Retry}AppService`,
`RemoteAuthorizeAppService`, `RemoteRelatedInstanceReader`) call another domain's **orchestrator**
— HTTP/JSON MVC controllers behind `dapr.io/app-protocol: http`. They can now travel over Dapr
service invocation when `ServiceDiscovery:Provider=dapr`; the wire is chosen per call from the
resolved `EndpointKind` by `RemoteTransportRouter`, and the Dapr shell (`DaprRemoteTransport`) sends
through the `HttpClient` returned by `DaprClient.CreateInvokeHttpClient()`. Architecture and
contracts: [Remote App Service Architecture](remote-app-service-architecture.md).

**Why `CreateInvokeHttpClient` and not `DaprClient.InvokeMethod*`.** In SDK 1.17.9 the entire
`InvokeMethod*` family — `InvokeMethodAsync`, `InvokeMethodWithResponseAsync`,
`InvokeMethodGrpcAsync`, every overload — carries
`[Obsolete("Recommended guidance is to use a native HTTP or gRPC client for service invocation")]`
(`DaprClient.cs` lines 448–753). `CreateInvokeHttpClient()` is not obsolete and is that "native HTTP
client": an `HttpClient` whose `InvocationHandler` rewrites an absolute `http://{appId}/{path}` to
`{endpoint}/v1.0/invoke/{appId}/method/{path}` via `UriBuilder(uri)`, resolves
`DAPR_HTTP_ENDPOINT`/`DAPR_HTTP_PORT`/`DAPR_API_TOKEN` through `DaprDefaults`, adds the token per
request and removes it in `finally`. The same move was made everywhere the obsolete family was
used — `RemoteInvokerService`'s HTTP branch and all eight Execution invokers — through one shared
type, `DaprServiceInvocationClient` (`BBT.Workflow.Execution.Abstractions`); a
`dotnet build --no-incremental` now reports zero CS0618 for `InvokeMethod*`.

**The app → sidecar hop is HTTP, deliberately.** `InvokeMethodGrpcAsync<TRequest,TResponse>`
requires `TRequest : IMessage` (Protobuf) and a callee implementing Dapr's AppCallback gRPC
service; the orchestrator is an HTTP/JSON app and implements neither — and the method is obsolete
anyway. So the SDK contributes the sidecar contract (endpoint, token, invoke URI) while the sidecar
contributes Name Resolution, sidecar-to-sidecar gRPC + mTLS, and the `resiliency-cross-domain.yaml`
circuit breaker. Moving these endpoints to gRPC is the same shape as Orchestration → Execution above
(a gRPC surface on the callee + Dapr gRPC proxying) and is a separate piece of work.

**Two details pinned by `DaprRemoteTransportTests`** — which run the SDK's real
`InvocationHandler` over a recording stub, so they observe exactly what the sidecar would receive:

- **Query strings survive verbatim.** `InvocationHandler` rewrites scheme/host/port/path and
  leaves the query alone, so `filter=a%20b` reaches the sidecar as `filter=a%20b`. The
  `CreateInvokeMethodRequest(…, queryStringParameters)` overload would have re-escaped each pair
  with `Uri.EscapeDataString` (`filter=a%2520b`), which is one more reason it is not used.
- **An unreachable callee does not fail the socket.** The sidecar answers `HTTP 500` with
  `{"errorCode":"ERR_DIRECT_INVOKE",…}`, which `MapToErrorAsync` would classify as a permanent
  remote 5xx. The shell converts it to `HttpRequestException` so the ~28
  `catch (HttpRequestException)` sites keep producing `Error.Transient("remote_network_error", …)`;
  a genuine callee 5xx (identified by the `_aether_error_format` header only a vNext app emits) still
  maps as a remote error. A socket failure to the sidecar is already a native `HttpRequestException`
  on this path.

**Trace shape.** The callee here is the other domain's orchestrator, which — like the Execution
host — receives Dapr's duplicated `traceparent` and tolerates it through
`DuplicateTolerantTraceContextPropagator` (installed in all four hosts' `Program.cs`). Each
cross-domain call therefore gains a caller-sidecar and a callee-sidecar span under one trace; the
callee sidecar's own span remains unreachable from the AppCallback header, exactly as documented
for Orchestration → Execution above.

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

With the fix in place — HTTP/1.1 on `4202` (from `ASPNETCORE_URLS`, Http1 only),
`Kestrel:GrpcPort=4212` (Http2 only), sidecar `--app-port 4212` + `--app-protocol grpc` — startup
no longer logs any Kestrel HTTP/2 warning. Direct, independent confirmation of both ports before
running any test:

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

### RESOLVED (was: KNOWN LIMITATION) — gRPC proxy mode split every task invocation into two traces

**Status: fixed and verified live on 2026-08-26.** gRPC proxy-mode invocations now produce **one
whole trace tree**, with Execution's server transaction parented inside the caller's trace. The
underlying Dapr defect is unchanged — the sidecar still delivers a malformed, W3C-invalid
`traceparent` — but the runtime now tolerates it instead of discarding it. The investigation that
got here is preserved below, unedited in substance, because the *reason* the mitigation lives where
it lives is the valuable part.

#### What was broken

Every task invocation over gRPC proxy mode yielded two separate top-level traces where the HTTP
path produces one continuous trace. Orchestration's own trace (e.g.
`93ddf309ef6bcad9201cf60f4cec3bdb`) contained the gRPC client span
(`bbt.workflow.execution.v1.TaskInvoker/Invoke`, `GrpcNetClient` instrumentation, correctly
parented under `Task.Invoke`) and the two sidecar-side `dapr-diagnostics` spans — and **no
`Microsoft.AspNetCore` transaction at all**. Execution's real work —
`Microsoft.AspNetCore POST /bbt.workflow.execution.v1.TaskInvoker/Invoke` →
`Invoke.http/execute-transfer` → the outbound provider call — landed in a **separate,
freshly-rooted** trace (e.g. `0f21fe1b6eb06dfe3a6d4dc5bed24a1b`, `parent: None`), with
`user_agent: grpc-go/1.73.0` confirming the request reaching Execution's ASP.NET Core pipeline came
from the Dapr sidecar's own Go gRPC client, not a raw proxy of the original stream. Timestamps
(~40ms apart) and durations matched — one logical call, split into two traces.

#### Root cause, confirmed empirically — three things were tested in order

1. *Hypothesis: the caller never sends `traceparent` on the wire.* Disproven directly. A temporary
   diagnostic on Execution's gRPC service (`context.RequestHeaders`) showed `traceparent` present
   on **every** call — .NET's `HttpClient` `DiagnosticsHandler` (which `Grpc.Net.Client`'s channel
   runs on) auto-injects it from `Activity.Current` on every outbound gRPC call, framework-level,
   independent of any OTel package. It was never missing.
2. *Hypothesis: sending it explicitly (from `TaskTraceContext.TraceParent`/`TraceState`, already
   populated from `Activity.Current` in `RemoteInvokerService.CreateTraceContext`) fixes it.*
   Tested and disproven. Adding an explicit `metadata.Add("traceparent", ...)` in
   `InvokeOverGrpcAsync` did not help, because of finding 3 — it only adds a second value to an
   already-broken header.
3. **The actual defect: `traceparent` arrives duplicated on every call**, regardless of whether
   this codebase sends it explicitly. The raw value Execution receives looks like:
   ```
   traceparent = 00-1c00cbe047937c981316a9a85f69bad6-52e645eaa63e82cb-01,00-1c00cbe047937c981316a9a85f69bad6-5c6c3f4a64d19975-01
   ```
   Same trace id, two different span ids, comma-joined into one value. This happens upstream of
   anything this codebase controls — on the Dapr sidecar's app-bound (AppCallback) hop, which
   re-issues its own gRPC call to the app rather than proxying the original HTTP/2 stream, and
   evidently stamps its own context alongside forwarding the original rather than replacing it. A
   value with more than one `traceparent` is **invalid per the W3C Trace Context spec** — a
   compliant receiver MUST treat it as if no trace context was present — which is exactly what
   ASP.NET Core's built-in hosting instrumentation does: it starts a fresh root `Activity`.

**This remains true.** Dapr still sends the malformed header; nothing below changes that. What
changed is that the runtime no longer throws the whole value away.

#### Why the body-based fallback could never close it

`TaskInvokeHandler.HandleAsync` calls `RestoreActivityFromBodyIfDetached(traceContext)`, using the
trace context carried in the *request body* (`TaskTraceContext.TraceParent`/`TraceState` — a clean,
single, valid value captured from `Activity.Current` on the orchestration side before it ever
touches the wire) as a fallback. It was confirmed working live: the disconnected trace's
`Microsoft.AspNetCore` transaction carried `labels.vnext_trace_mismatch: "true"` and
`span.links: [{trace.id, span.id}]` pointing at the caller.

**But it cannot merge two traces into one**, for a structural reason rather than a bug in that
code: `Activity.ParentId` is fixed at `Activity.Start()` and cannot be changed afterward, and
ASP.NET Core's hosting layer has **already started** the `Microsoft.AspNetCore` transaction's
`Activity` — reading, and discarding, the malformed incoming `traceparent` — before
`TaskInvokeHandler`'s code ever runs. By the time the fallback executes, `Activity.Current` is a
non-null, already-rooted activity. The only thing available at that point is exactly what the code
already does: link, don't re-parent. That fallback stays in place as a defence in depth; on the
fixed path it simply never fires, because the wire context now matches.

#### The mitigation: a duplicate-tolerant propagator

`src/BBT.Workflow.HttpApi.Shared/Telemetry/DuplicateTolerantTraceContextPropagator.cs`, installed in
`execution/BBT.Workflow.Execution.HttpApi.Host/Program.cs`.

**Why a propagator is the only seam.** ASP.NET Core's hosting layer builds the incoming request's
`Activity` from `DistributedContextPropagator.Current` *before* any application code runs. A
propagator therefore runs strictly earlier than every middleware, filter and handler — it is the
last point at which the malformed value can still be corrected, and the immutability of
`Activity.ParentId` rules out everything later.

It wraps the propagator that would otherwise be in force and delegates `Fields`, `Inject` and
`ExtractBaggage` **verbatim**; only `ExtractTraceIdAndState` is corrected. The rules:

| Input | Behavior |
|---|---|
| Single well-formed value | Delegated **untouched** — zero behavior change on every normal request |
| Multiple values, all sharing one trace-id | Collapsed to the **last** value |
| Values with **differing** trace-ids | Treated as **absent** (W3C: an uninterpretable value must not be guessed at) |
| Anything else malformed, or a throwing carrier | Treated as **absent**, never throws |
| Duplication as one comma-joined string **or** as separate header values | Both handled identically |

**Which duplicate wins, and why — determined empirically, not by guesswork.** The two span ids in
the captured header were identified against the real trace they came from
(`1c00cbe047937c981316a9a85f69bad6`) in Elastic:

```
Task.Invoke                                                        0904b2437acd8f3c
└─ bbt.workflow.execution.v1.TaskInvoker/Invoke   (GrpcNetClient)  bfa61e1b2e7cd43a
   └─ POST                                        (System.Net.Http) 52e645eaa63e82cb   <- FIRST value
      └─ /...TaskInvoker/Invoke      (dapr-diagnostics, CALLER sidecar) 5c6c3f4a64d19975   <- LAST value
         └─ /...TaskInvoker/Invoke   (dapr-diagnostics, CALLEE sidecar) 366bc7bc14789e4d
```

So the **first** value is the app's own outbound `System.Net.Http` client span, and the **last** is
the **caller** sidecar's span. The callee sidecar's own span (`366bc7bc…`) appears in **neither** —
the AppCallback hop appends the context it *received* rather than the one it *created* — so "hang
the app's server span under the callee sidecar", the ideal, is simply not reachable from this
header. The last value is the deepest node the header actually offers: it makes the app's server
span a **sibling** of the callee sidecar transaction rather than skipping a level up to the HTTP
client span, which is what taking the first value would produce. Either choice preserves the
trace-id — which is what makes the tree whole; the span-id choice only decides which node it hangs
under. The general HTTP rule points the same way: header lists append, so the hop nearest the app
wrote last.

**Registration is unconditional and deliberately visible in `Program.cs`,** not tucked inside
`AddExecutionApiModule()`. Two decisions worth stating:

- *Visible, because it is a process-global mutation.* `DistributedContextPropagator.Current`
  changes how **every** inbound request in the process is parented. Hiding that three DI extensions
  deep would be a trap for the next reader.
- *Unconditional, not gated on the gRPC transport.* The transport is chosen by the **orchestration**
  side's `ExecutionApi:Transport`, which the Execution process cannot see — a local gate would be
  guessing at another service's configuration. And the decorator is a strict no-op for well-formed
  input, so gating buys nothing and costs a config coupling.
- *It must stay above `WebApplication.CreateBuilder`.* ASP.NET Core's web-host bootstrap captures
  `DistributedContextPropagator.Current` into DI as a singleton **instance** while the builder is
  being constructed, and hosting resolves the propagator from there. Assigning after
  `CreateBuilder` leaves the captured instance — and therefore request parenting — unchanged.

Unit tests: `test/BBT.Workflow.Application.Tests/Telemetry/DuplicateTolerantTraceContextPropagatorTests.cs`
(23 cases, built on the exact captured header above rather than a synthetic one), including a
delegation-parity test against the default propagator for both `Inject` and the well-formed
extraction path.

#### Live verification — before and after

Both measured on the same `MoneyTransferTests` scenario, in gRPC proxy mode, in the same
environment. Query: Elastic `http://localhost:9200`, indices `.ds-traces-apm*,traces-apm*`.

**BEFORE** — Execution's `Microsoft.AspNetCore` transaction (`user_agent: grpc-go/1.73.0`), every
pre-fix gRPC run:

```
2026-08-26T14:38:50.407Z  trace=df0a6dbbb3f8839821b7e913f980a64e  parent=None
2026-08-26T14:36:40.336Z  trace=3f42ac2641d9c9b3af9e8b2627e58dd0  parent=None   mismatch=true  links=true
2026-08-26T14:36:38.862Z  trace=d9d25015bc322b66eb5e3a58a931fb42  parent=None   mismatch=true  links=true
2026-08-26T14:31:43.510Z  trace=259258acd8af87a584ffd6bbdcbd50f4  parent=None   mismatch=true  links=true
2026-08-26T14:31:42.070Z  trace=27aed8d7eb1030de8cf881ec30413063  parent=None   mismatch=true  links=true
2026-08-26T14:25:13.303Z  trace=9c799562f3c435f7baaeeefa67b0a477  parent=None
```

`parent=None` on every row, and every one of those `trace.id`s differs from the caller's — separate
root traces. The caller's trace `1c00cbe047937c981316a9a85f69bad6` held **84** documents and
contained no Execution-side application span at all.

**AFTER** — same query, post-fix run:

```
2026-08-26T15:56:27.356Z  trace=6f2ad3d6f165566dd2351fb26a00ece5  parent=6f12c508746c4c8e  mismatch=None  links=false
2026-08-26T15:56:25.856Z  trace=265f5094c2139ef7be78e1f58a58d66b  parent=58809a2dbf411aa7  mismatch=None  links=false
```

Non-null `parent.id`, and the `trace.id` is the **caller's own trace**. The `vnext_trace_mismatch`
label and the `span.links` bridge are both gone — the body fallback no longer needs to fire. The
caller's trace grew from 84 to **117** documents: Execution's ~33 spans moved *into* it.

The full spine of trace `6f2ad3d6f165566dd2351fb26a00ece5`, one tree end to end:

```
[txn] PATCH .../instances/{instance}/transitions/{transitionKey}   vnext-app            e202fc3e1eae5e1f (root)
└─ [txn] TransitionJob.Execute/approve-push                        vnext-app            11c96f47088b3b9e
   └─ … Task.Invoke                                                vnext-app            cd472f0268fcdf82
      └─ bbt.workflow.execution.v1.TaskInvoker/Invoke  (GrpcNetClient)                   cf60d3a73760d6f2
         └─ POST                                       (System.Net.Http)                 f88921d6de8baf30
            └─ /...TaskInvoker/Invoke        (dapr-diagnostics, CALLER sidecar)          6f12c508746c4c8e
               ├─ [txn] /...TaskInvoker/Invoke (dapr-diagnostics, CALLEE sidecar)        2350f467daa48110
               └─ [txn] POST /...TaskInvoker/Invoke  (Microsoft.AspNetCore,
                        vnext-execution-app, ua=grpc-go/1.73.0)                          dbd6e64139561034   <-- THE FIX
                  └─ Invoke.http/execute-transfer  (BBT.Workflow.Execution.Invokers)     eeebf66a5329bb41
                     └─ POST  (System.Net.Http, outbound to MockLab)
```

The app's server transaction lands as a sibling of the callee sidecar transaction under the caller
sidecar's span — exactly the placement the "last value wins" analysis predicts, confirming the
choice behaves as reasoned rather than by luck.

**Orphan spans: unchanged.** 3 orphans in the post-fix trace (`System.Net.Http POST`, the
pre-existing deferred pub/sub-publish spans), the same 3 as the pre-fix HTTP baseline
(`6b85c250d727e7bdd98a620f0e019563`, 118 docs) and the same 3 as the pre-fix gRPC trace. The fix
introduces no new orphan pattern.

**Business correctness, both transports:** `MoneyTransferTests` `Failed: 0, Passed: 5` over gRPC
(2026-08-26T15:56Z) and `Failed: 0, Passed: 5` again after reverting to the HTTP default
(2026-08-26T16:0xZ). gRPC remains **opt-in**; the committed state is `ExecutionApi:Transport = http`
with the sidecar on `--app-port 4202` and no `--app-protocol`.

#### What is still true

- Dapr still sends a duplicated, W3C-invalid `traceparent` on the AppCallback hop. This is a Dapr
  defect, external to this repository, and the propagator is a **tolerance layer**, not a cure. If
  Dapr's behavior changes to send a single valid value, the propagator becomes inert on its own
  (single well-formed value → delegated untouched) and needs no removal.
- The app's server span hangs under the **caller** sidecar's span, not the callee's, because the
  callee sidecar's span id is not present in the header at all. This is a cosmetic one-level
  difference in the tree, not a break in it.
- If Dapr ever starts appending values from *different* traces, the propagator treats the header as
  absent and the old split-trace behavior returns — deliberately, since guessing between unrelated
  traces would be worse than a clean re-root.

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

**Distributed trace continuity across the gRPC hop is now proven, having first been disproven and
then fixed.** The investigation correctly root-caused the split to Dapr's AppCallback hop delivering
a duplicated, W3C-invalid `traceparent` — external to this repository — and correctly concluded that
nothing at or after `TaskInvokeHandler` could repair it, because `Activity.ParentId` is immutable
once ASP.NET Core's hosting layer has started the request's `Activity`. What that reasoning missed is
that `DistributedContextPropagator.Current` runs *before* hosting starts that `Activity`, and is
therefore a seam that does exist. `DuplicateTolerantTraceContextPropagator` (installed in the
Execution host's `Program.cs`) collapses the duplicated header to a single valid value while leaving
well-formed input untouched. Verified live: Execution's `Microsoft.AspNetCore` transaction now
carries the caller's `trace.id` and a non-null `parent.id`, the caller's trace grew from 84 to 117
documents as Execution's spans moved into it, and the `span.links` / `vnext_trace_mismatch` fallback
no longer fires. Dapr's defect is unchanged; the runtime now tolerates it. Full before/after evidence
in the "RESOLVED (was: KNOWN LIMITATION)" section above.

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
ports are relocatable — the HTTP/1.1 port via `ASPNETCORE_URLS`/`--urls` (the platform's existing
mechanism, not a Kestrel-specific key), the gRPC port via `Kestrel:GrpcPort` in `appsettings.json`
(overridable per-environment) — so Helm can also relocate the ports themselves if `4202`/`4212`
collide with something else in a given cluster — as long as the two Dapr annotations are updated
to match.
