# Dapr Invocation Transport: What Uses gRPC, What Stays HTTP, and Why

> **Scope note (issue #1007):** the "Orchestration → Execution task invoke" hop this document
> analyzes is now only exercised for task types the invocation router resolves **Remote**. Four
> wire types (`http`, `daprservice`, `soap`, `statestore`) run in-process inside
> Orchestration by shipped default and never reach this hop at all. Everything below still
> applies exactly as written whenever a call does cross to Execution — by default that means
> `daprbinding`, `daprhttpendpoint`, `daprpubsub`, `daprconversation`, `python`, the trigger/query
> types, and any of the four local types reverted to Remote via configuration. See
> [Task Invocation Routing](task-invocation-routing.md) for the resolution order and the config.

## TL;DR

| Path | Transport | Why |
|---|---|---|
| Orchestration → Execution task invoke | Dapr HTTP service invocation (`DaprServiceInvocationClient` → `POST api/v1/execution/invoke/{type}/{key}`) | The only transport. The opt-in gRPC proxy mode was removed in 0.0.100 (issue #1056) — see "History: gRPC proxy mode (removed)" |
| DaprServiceTask → external domain apps | HTTP API (unchanged) | gRPC→HTTP invocation is deprecated for removal; targets are HTTP apps |
| App → sidecar state/lock/pubsub calls | SDK-chosen (unchanged) | Not in scope of this document |
| Cross-domain `Remote*` services (orchestrator → other domain's orchestrator) | `DaprClient.CreateInvokeHttpClient()` (SDK `InvocationHandler`, HTTP to the sidecar), opt-in via `ServiceDiscovery:Provider=dapr` | The whole `DaprClient.InvokeMethod*` family is `[Obsolete]` in 1.17; `CreateInvokeHttpClient` is the surface its message points at. Targets are HTTP/JSON controllers, so gRPC on the first hop is not available. See "Cross-domain Remote* services" below. |

> **Dapr sidecar gRPC is not what was removed.** Sidecar-to-sidecar traffic is always gRPC, the
> apps still talk to their sidecar's gRPC API (`DAPR_GRPC_PORT`) for state, lock and pub/sub, and
> OTLP export may still use gRPC. Only the app-level Orchestration → Execution gRPC service is gone.

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
that method choice is what the DaprServiceTask verdict below turns on.

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
  callers toward.

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
gRPC. At that point per-target proxy mode (`CreateInvocationInvoker`) is the road back in. Nothing
about today's HTTP-target contract changes until then.
## Orchestration → Execution

Execution's Dapr-inbound surface is a single endpoint
(`POST api/v{version}/execution/invoke/{type}/{key}`, `ExecutionController` → `TaskInvokeHandler`)
with no `[Topic]` subscriptions, no job callbacks and no other inbound service invocation.
Orchestration's `RemoteInvokerService` reaches it through `DaprServiceInvocationClient`
(`DaprClient.CreateInvokeHttpClient()`), i.e. HTTP to its own sidecar, gRPC + mTLS between the
sidecars, and HTTP from Execution's sidecar to the app (`dapr.io/app-protocol: http`, the default).

Execution hosts one HTTP/1.1 surface — the controller, health probes and swagger — on the port its
hosting URL declares (`ASPNETCORE_URLS`: `http://+:5000` in the image and the Helm chart,
`4202` locally). `Kestrel:Limits` is the only Kestrel configuration in code.

The wire contract is plain JSON: the request is `TaskInvokeRequest { Envelope, TraceContext }`, the
response is `TaskInvokeResponse { Success, ErrorMessage, Result, ExecutionDurationMs }`
(`TaskDtoWireContractParityTests` pins the two DTO families to each other).

`RemoteInvokerService` reads `ExecutionApi:AppId` and `ExecutionApi:InvocationTimeoutSeconds = 60`,
and performs no transport retry by design: the sidecar resiliency policy for this call is
circuit-breaker-only, because retrying would re-invoke tasks that can have side effects — a decision
recorded in the class's own remarks, not an oversight.

### History: gRPC proxy mode (removed)

From 0.0.87 to 0.0.99 this hop could optionally run as a Dapr gRPC proxy-mode call: a
`task_invoker.proto` `TaskInvoker` service on Execution, a `GrpcTaskInvokerClientProvider` on
Orchestration, selected by `ExecutionApi:Transport = grpc`. Because Kestrel cannot negotiate HTTP/1.1
and HTTP/2 on one cleartext port (no ALPN without TLS), Execution bound a second, HTTP/2-only h2c port
(`Kestrel:GrpcPort`, Helm `execution.grpcPort`), and an environment had to move three settings
together: `ExecutionApi__Transport=grpc`, `dapr.io/app-protocol: grpc` and `dapr.io/app-port` →
the gRPC port. No shipped default ever did, and after issue #1007 most hot task types run in-process
on Orchestration and never cross this hop, so the stack was removed in 0.0.100 (issue #1056).

What that means now:

- `ExecutionApi:Transport` is not read; a leftover `grpc` value is ignored and the call goes over HTTP.
- Execution binds no h2c port and serves no gRPC service. An environment that had moved the
  Execution sidecar to `app-protocol: grpc` and the gRPC port must return to `http` and the HTTP port
  (the Helm chart defaults).
- Recorded in `vnext-meta/deprecations.json` as `execution-grpc-transport-removed`.

The design notes and the verification passes are in the git history of this file (before #1056).
The one finding that outlives the transport — Dapr's duplicated `traceparent` — is kept below,
because `DuplicateTolerantTraceContextPropagator` still depends on it.

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
circuit breaker. Moving these endpoints to gRPC would need a gRPC surface on the callee plus Dapr gRPC
proxying, and is a separate piece of work.

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
callee sidecar's own span remains unreachable from the AppCallback header, as documented in
"Dapr's duplicated `traceparent`" below.
## Dapr's duplicated `traceparent` (found on the gRPC hop, still tolerated)

> Historical evidence: this was found and verified on the removed gRPC proxy-mode hop ("Task 6",
> 2026-08-26). The propagator stays installed in every host because the same malformed header also
> reaches HTTP callees (see "Trace shape" under Cross-domain `Remote*` services).

**Status: fixed and verified live on 2026-08-26.** gRPC proxy-mode invocations now produce **one
whole trace tree**, with Execution's server transaction parented inside the caller's trace. The
underlying Dapr defect is unchanged — the sidecar still delivers a malformed, W3C-invalid
`traceparent` — but the runtime now tolerates it instead of discarding it. The investigation that
got here is preserved below, unedited in substance, because the *reason* the mitigation lives where
it lives is the valuable part.

### What was broken

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

### Root cause, confirmed empirically — three things were tested in order

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

### Why the body-based fallback could never close it

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

### The mitigation: a duplicate-tolerant propagator

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

### Live verification — before and after

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

> Superseded for the orphans themselves, 2026-09-13: that pattern — an app-side `System.Net.Http`
> span whose parent was never exported — was Aether's HttpClient trace filter writing a filtered
> activity's id to the wire, and is fixed upstream. The measurement above stands as a record of the
> transport change; the orphan count it reports is no longer the current behaviour.

**Business correctness, both transports:** `MoneyTransferTests` `Failed: 0, Passed: 5` over gRPC
(2026-08-26T15:56Z) and `Failed: 0, Passed: 5` again after reverting to the HTTP default
(2026-08-26T16:0xZ). The gRPC transport itself was removed in 0.0.100 (issue #1056).

### What is still true

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
