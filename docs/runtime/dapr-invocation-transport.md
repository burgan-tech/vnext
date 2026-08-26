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

## Verification (filled by Task 6)

_Pending: the observed trace tree from the first end-to-end gRPC run lands here._
