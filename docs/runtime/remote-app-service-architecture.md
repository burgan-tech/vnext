# Remote App Service Architecture

## Purpose

Remote app services are infrastructure adapters for calling another vNext runtime over
HTTP. They support multi-domain and distributed deployments while preserving application
service contracts for callers.

## Boundaries

Remote app services live in Infrastructure. They should not decide domain behavior; they
translate typed application inputs into HTTP requests, resolve endpoints, forward safe
headers, and map HTTP responses back to result types.

## Architecture Flow

1. Routed gateway chooses remote path.
2. Remote app service resolves target domain endpoint.
3. URL is built with `InstanceUrlTemplates` and configured API version.
4. Request body, query parameters, ETag headers, and current-user headers are attached.
5. HTTP client sends request using configured retry, timeout, and circuit breaker policy.
6. Response helper maps success, conditional response, validation error, or transient error.

## Contracts

| Service | Interface | Purpose |
| --- | --- | --- |
| Instance command | `IRemoteInstanceCommandAppService` | Start, transition, complete, mark busy, subflow callbacks. |
| Instance query | `IRemoteInstanceQueryAppService` | Instance, data, state, view, hierarchy, lists. |
| Retry | `IRemoteInstanceRetryAppService` | Retry faulted or incident-backed instances. |
| Authorization | `IRemoteAuthorizeAppService` | Authorize and permissions matrix across domains. |

## Failure Modes

- Endpoint cannot be resolved.
- Remote returns validation or domain error.
- Remote returns `304 Not Modified` for conditional data/state reads.
- Network timeout, retry exhaustion, or circuit breaker open.
- Remote response body cannot be deserialized into expected contract.

## Observability

HTTP clients should include a runtime user-agent and forward correlation/current-user
headers. Remote failures should log domain, endpoint kind, path, status code, and mapped
error code without logging sensitive payloads.

## Change Safety

- Add remote methods only after the local interface contract is clear.
- Preserve ETag and current-user forwarding behavior.
- Keep retry/circuit-breaker policy in remote service registration.
- Do not encode URLs in multiple places; use `InstanceUrlTemplates`.

## References

- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceCommandAppService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceQueryAppService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceRetryAppService.cs`
- `src/BBT.Workflow.Infrastructure/Authorization/Remote/RemoteAuthorizeAppService.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Extensions/RemoteServiceExtensions.cs`
- `src/BBT.Workflow.Infrastructure/Remote/RemoteHttpResponseHelper.cs`
- `src/BBT.Workflow.Infrastructure/Remote/CurrentUserForwardHeadersHelper.cs`

