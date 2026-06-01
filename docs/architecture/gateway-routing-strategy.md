# Gateway Routing Strategy

## Purpose

Gateway routing lets the runtime call the same use case locally or remotely. This keeps
controllers and task executors stable while deployment topology changes between single
runtime, multi-domain, and remote-domain scenarios.

## Boundaries

| Type | Responsibility |
| --- | --- |
| Routed gateway | Chooses local or remote implementation based on input domain and runtime ownership. |
| Local gateway | Runs the application service in a scoped local workflow context. |
| Remote gateway | Delegates to a remote app service. |
| Remote app service | Builds HTTP requests, resolves endpoint, forwards headers, handles remote response. |
| Domain discovery | Resolves a domain to URL or Dapr app id. |

## Architecture Flow

1. A caller invokes an interface such as `IInstanceCommandGateway`.
2. The routed gateway checks whether the target domain is local.
3. Local path creates a service scope and executes the application service under the workflow schema context.
4. Remote path calls the remote app service.
5. Remote app service resolves endpoint through `IDomainDiscoveryResolver`.
6. Request headers are merged with current-user forwarding headers.
7. Response is converted back into the application result shape.

## Contracts

| Contract | Local path | Remote path |
| --- | --- | --- |
| Instance commands | Application service in same process | `RemoteInstanceCommandAppService` over HTTP. |
| Instance queries | Application query service in same process | `RemoteInstanceQueryAppService` over HTTP. |
| Retry | Local retry service | `RemoteInstanceRetryAppService` over HTTP. |
| Authorization | Local authorization service | `RemoteAuthorizeAppService` over HTTP. |

Remote paths should preserve API version, domain, workflow, instance id/key, query
parameters, ETag headers, and current-user context when the operation is user-scoped.

## Failure Modes

- Discovery disabled or missing endpoint returns `DomainDiscoveryFailed`.
- Dapr-preferred discovery falls back to URL when no app id exists.
- Remote network errors are mapped to transient errors.
- Circuit breaker, timeout, and retry policies are configured through `RemoteOptions`.
- Restricted headers must not be forwarded to remote services.

## Observability

Remote HTTP clients set a runtime user-agent and should carry correlation and current-user
headers. Gateway decisions should be visible through logs at the routed and remote app
service layers when troubleshooting cross-domain calls.

## Change Safety

- Keep routing decisions in routed gateways, not in controllers.
- Keep HTTP URL construction centralized in `InstanceUrlTemplates`.
- New remote operations need local and remote gateway coverage.
- Add tests for local-vs-remote selection when introducing a new gateway method.

## References

- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceRetryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedAuthorizeGateway.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/`
- `src/BBT.Workflow.Infrastructure/Authorization/Remote/RemoteAuthorizeAppService.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Configuration/RemoteOptions.cs`

