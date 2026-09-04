# Remote App Service Architecture

## Purpose

Remote app services are infrastructure adapters for calling another vNext runtime in a
different domain. They support multi-domain and distributed deployments while preserving
application service contracts for callers. Since 2026-09 they are transport-agnostic: the same
service sends over plain HTTP or over Dapr service invocation depending on how the target domain
was resolved.

## Boundaries

Remote app services live in Infrastructure. They should not decide domain behavior; they
translate typed application inputs into a logical request (method, relative path, body, safe
headers), resolve the target endpoint, hand the request to the **transport shell**, and map the
response back to result types. They never see an `HttpClient` or a `DaprClient`.

## Architecture Flow

1. Routed gateway chooses remote path.
2. Remote app service resolves the target domain through `IDomainDiscoveryResolver`. The
   selected **discovery provider** (`ServiceDiscovery:Provider`) decides the endpoint's
   `EndpointKind`:
   - `http` — registry-supplied base URL, `EndpointKind.Url` (the default; pre-Dapr behaviour).
   - `dapr` — convention-derived app-id `vnext-{domain}-app[.{namespace}]`, `EndpointKind.Dapr`. The
     namespace suffix comes from `ServiceDiscovery:Dapr:NamespaceTemplate` (only token `{domain}`,
     e.g. `stage-vnext-{domain}`); the Helm chart renders it from the release namespace
     (`vnext.crossNamespaceTemplate`, override `global.dapr.crossNamespaceTemplate`), empty means
     single-namespace. By default (`RequireRegistryEntry=false`) no registry call is made.
3. Relative path is built with `InstanceUrlTemplates` and the configured API version.
4. The service calls `IRemoteTransport<TClient>.SendAsync(endpoint, method, relativePath, configure)`.
   `configure` attaches body, query-derived headers, ETag and current-user headers to the message
   the shell builds — it may run once per retry attempt, so it must only populate the message.
5. `RemoteTransportRouter<TClient>` dispatches on `endpoint.Kind` — nothing else:
   - `HttpRemoteTransport<TClient>` composes `BaseUrl + relativePath` and sends through the named
     `IHttpClientFactory` client (`typeof(TClient).Name`) carrying timeout / profile-gated retry /
     circuit breaker / decompression / `X-Internal-Operation`.
   - `DaprRemoteTransport<TClient>` sends `http://{appId}/{relativePath}` through the SDK's
     invocation `HttpClient` (`DaprClient.CreateInvokeHttpClient()`), whose `InvocationHandler`
     rewrites it to the sidecar's `/v1.0/invoke/{appId}/method/…`; the same `RemotePolicyFactory`
     policies are applied programmatically around each send.
6. Response helper maps success, conditional response, validation error, or transient error.

## Transport shell

| Type | Role |
| --- | --- |
| `IRemoteTransport<TClient>` | The contract the services depend on. Generic over the client interface so profile and circuit-breaker state stay per client. |
| `HttpRemoteTransport<TClient>` | Plain HTTP over the named factory client. Byte-for-byte the pre-Dapr path; what `ServiceDiscovery:Provider=http` (and a `DomainOverrides` value of `url`) route to — a real rollback. |
| `DaprRemoteTransport<TClient>` | Dapr service invocation through `DaprClient.CreateInvokeHttpClient()` — the SDK's **non-obsolete** invocation surface. Its `InvocationHandler` owns `DAPR_HTTP_ENDPOINT`/`DAPR_HTTP_PORT`/`DAPR_API_TOKEN` and the invoke URI. |
| `RemoteTransportRouter<TClient>` | Kind-based dispatch. Resolves the Dapr shell lazily and fails with `HttpRequestException` when no Dapr shell is registered (a missing sidecar surfaces the same way, as a connection failure). |
| `RemotePolicyFactory` | Timeout → [Retry] → Circuit breaker, shared by both shells. Retry only for `RemoteServiceProfile.Read`. |

### Why the shell builds the request

Each shell constructs its own `HttpRequestMessage` from `(endpoint, method, relativePath)`. Because
the shell owns construction, no `dapr://` pseudo-scheme participates in routing — the
`DiscoveryEndpoint.BaseUrl` of a Dapr endpoint is `dapr://{appId}/` for logging only — and a
retrying transport can build a fresh message per attempt (a sent `HttpRequestMessage` cannot be
sent again).

### Which SDK surface, and why

In Dapr .NET SDK 1.17 the **entire** `DaprClient.InvokeMethod*` family — `InvokeMethodAsync`,
`InvokeMethodWithResponseAsync`, `InvokeMethodGrpcAsync` — carries
`[Obsolete("Recommended guidance is to use a native HTTP or gRPC client for service invocation")]`
(every runtime call site — `RemoteInvokerService`'s HTTP branch and the eight Execution invokers —
was moved off it onto the shared `DaprServiceInvocationClient` in `BBT.Workflow.Execution.Abstractions`).
The non-obsolete surface the message points at is `DaprClient.CreateInvokeHttpClient()`: an
`HttpClient` carrying the SDK's `InvocationHandler`, which rewrites an absolute
`http://{appId}/{path}` to `{DAPR_HTTP_ENDPOINT}/v1.0/invoke/{appId}/method/{path}`, attaches
`DAPR_API_TOKEN` per request and strips it again. `DaprOrchestrationForwarder` already uses it.

### Query strings under Dapr

`InvocationHandler` rewrites scheme, host, port and path through `UriBuilder(uri)` and leaves the
query untouched, so values the services already escaped arrive byte-identical on both shells.
(`CreateInvokeMethodRequest`'s `queryStringParameters` overload would re-escape them with
`Uri.EscapeDataString` — one more reason it is not used.)

### What Dapr adds — and what it does not

Through the sidecar the call gains Name Resolution, sidecar-to-sidecar mTLS, and the cross-domain
circuit breaker in `resiliency-cross-domain.yaml`. The app → sidecar hop stays **HTTP**: gRPC on
that hop would require Protobuf bodies and a gRPC callee (the orchestrator is an HTTP/JSON app), and
the SDK's gRPC invocation methods are obsolete like the rest of the family. Moving these endpoints
to gRPC would mean a gRPC surface on the orchestrator plus Dapr gRPC proxying — the
Orchestration → Execution precedent — and is out of scope here.

## Contracts

| Service | Interface | Profile | Purpose |
| --- | --- | --- | --- |
| Instance command | `IRemoteInstanceCommandAppService` | Mutating | Start, transition, complete, mark busy, subflow callbacks. |
| Instance query | `IRemoteInstanceQueryAppService` | Read | Instance, data, state, view, hierarchy, lists. |
| Retry | `IRemoteInstanceRetryAppService` | Mutating | Retry faulted or incident-backed instances. |
| Authorization | `IRemoteAuthorizeAppService` | Read | Authorize and permissions matrix across domains. |
| Related data | `RemoteRelatedInstanceReader` | Read | `internal/related-data` (+ batch); system identity, no header forwarding. |

**Profile** is the retry rule. Mutating endpoints are attempted exactly once by the transport: a
duplicate `instances/start` or `internal/subflow-forward` is data corruption, not a slow call, and
only the user-defined error boundary knows whether repeating a given transition is safe. This
cannot be expressed in Dapr resiliency (targets are app-ids, `retry.matching` filters by status
code only), so it lives in `RemotePolicyFactory`. `vNextApi:EnableRetryOnMutating` is the
emergency reversal and should stay `false`.

## Failure Modes

- Endpoint cannot be resolved (registry miss under `http`; under `dapr` only when
  `ServiceDiscovery:Dapr:RequireRegistryEntry=true` — the default is `false`, pure convention with no
  registry call, so an unknown domain surfaces later as the sidecar's `ERR_DIRECT_INVOKE`).
- Remote returns validation or domain error (`_aether_error_format` body).
- Remote returns `304 Not Modified` for conditional data/state reads.
- Network timeout, retry exhaustion, or circuit breaker open.
- **Sidecar cannot reach the callee.** This does not fail the socket: the sidecar answers
  `HTTP 500` with `{"errorCode":"ERR_DIRECT_INVOKE",…}`. `DaprRemoteTransport` converts it into
  `HttpRequestException` (a socket failure to the sidecar already is one on this path), so
  every service still produces `Error.Transient("remote_network_error", …)` and error boundaries keep
  treating it as transport. The predicate is narrow — 5xx, no `_aether_error_format` header, body
  `errorCode` starting `ERR_` — so a genuine callee 500 still maps normally.
- Remote response body cannot be deserialized into expected contract.

## Observability

HTTP clients include a runtime user-agent and forward correlation/current-user headers. The
`Discovery.Resolve/{domain}` span carries `vnext.discovery.provider` (`http` | `dapr`),
`vnext.discovery.resolution` (`convention` | `registry` | `cache`), `vnext.dapr.app_id` and
`vnext.dapr.namespace` — the primary signal for watching a per-domain rollout. Under Dapr each
cross-domain call additionally produces caller-sidecar and callee-sidecar spans; the callee
orchestrator tolerates the duplicated `traceparent` Dapr delivers via
`DuplicateTolerantTraceContextPropagator` (see
[Dapr Invocation Transport](dapr-invocation-transport.md)). Remote failures log domain, endpoint
kind, path, status code, and mapped error code without logging sensitive payloads.

## Change Safety

- Add remote methods only after the local interface contract is clear.
- Preserve ETag and current-user forwarding behavior.
- New remote methods call `transport.SendAsync(...)`; never take an `HttpClient` or `DaprClient`.
- Keep everything inside `configure` idempotent — it runs once per attempt on a fresh message.
- Keep retry/circuit-breaker policy in `RemotePolicyFactory`; register clients through
  `AddRemoteService<TClient,TImpl>(options, profile)` with the correct `RemoteServiceProfile`.
- Do not encode URLs in multiple places; use `InstanceUrlTemplates`. Do not pass query pairs to
  the SDK (see "Query strings under Dapr").
- Do not route on the `dapr://` placeholder; route on `EndpointKind`.

## Local development

The cluster resolves app-ids with Dapr's `kubernetes` name resolver
(`{{.ID}}-dapr.{{.Namespace}}.svc.cluster.local:{{.Port}}`); docker-compose has no namespaces,
no Sentry and no cluster DNS, so `etc/*/dapr/config.yaml` pins the self-hosted `mdns` resolver
explicitly (every sidecar announces `<app-id> -> <ip>:<internal grpc port>` on the bridge). The
`{app-id}-dapr` network alias and the explicit `--dapr-internal-grpc-port` per sidecar in
`etc/docker/docker-compose.yml` are conveniences, not requirements. `nameformat` was the first
choice and does **not** work here: the `daprio/daprd` 1.16.x image this compose runs has no such
resolver ("couldn't find name resolver nameformat/v1") and the sidecar then starts with no
resolver at all, answering 500 to every invoke. Application configuration is identical in both
environments.

A three-domain lab (`core`, `partner`, `discovery`) for exercising this path end to end lives in
vnext-example under `labs/cross-domain/` (`lab.sh up`); it runs the vnext-runtime compose template,
where the sidecars share the app container's network namespace and Dapr's default self-hosted mDNS
resolver is sufficient. Its app-ids are `vnext-app-{domain}`, so it depends on the registry `appId`
override rather than the convention and therefore sets both `ServiceDiscovery:Dapr:RequireRegistryEntry`
and `ServiceDiscovery:Dapr:PreferRegistryAppId` to `true` (the runtime default reads the registry not at
all). See the `cross-domain-lab` skill in `.claude/skills/`.

## References

- `src/BBT.Workflow.Infrastructure/Remote/Transport/IRemoteTransport.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Transport/HttpRemoteTransport.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Transport/DaprRemoteTransport.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Transport/RemoteTransportRouter.cs`
- `src/BBT.Workflow.Infrastructure/Remote/RemotePolicyFactory.cs`
- `src/BBT.Workflow.Infrastructure/Remote/RemoteServiceProfile.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Extensions/RemoteServiceExtensions.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/{DomainDiscoveryProviderBase,HttpDomainDiscoveryProvider,DaprDomainDiscoveryProvider,DiscoveryRegistryClient}.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceCommandAppService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceQueryAppService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceRetryAppService.cs`
- `src/BBT.Workflow.Infrastructure/Authorization/Remote/RemoteAuthorizeAppService.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RemoteRelatedInstanceReader.cs`
- `src/BBT.Workflow.Infrastructure/Remote/RemoteHttpResponseHelper.cs`
- `src/BBT.Workflow.Infrastructure/Remote/CurrentUserForwardHeadersHelper.cs`
- `src/BBT.Workflow.Execution.Abstractions/VNextAppIds.cs`
