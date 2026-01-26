# Remote Routing and Discovery

## Overview

The runtime routes instance operations either locally or to a remote domain. This routing is
implemented with gateways, remote HTTP clients, and a service discovery layer that resolves the
target endpoint per domain.

## Routing Model

### Gateways (Application → Infrastructure)

Interfaces:

- `IInstanceCommandGateway`
- `IInstanceQueryGateway`

Implementations:

- `RoutedInstanceCommandGateway` / `RoutedInstanceQueryGateway`
- `LocalInstanceCommandGateway` / `LocalInstanceQueryGateway`
- `RemoteInstanceCommandGateway` / `RemoteInstanceQueryGateway`

Routing decision:

- `Routed*` gateways use `IRuntimeInfoProvider.IsDomainMatch()` to choose **local** or **remote**.

Local execution:

- Uses `ICurrentSchema` to switch schema.
- Executes through `IInstanceCommandAppService` / `IInstanceQueryAppService`.
- Wraps subflow operations with `IUnitOfWorkManager` when needed.

Remote execution:

- Delegates to `IRemoteInstanceCommandAppService` and `IRemoteInstanceQueryAppService`.

## Remote Instance Clients

Remote clients live in `BBT.Workflow.Infrastructure/Instances/Remote` and:

- Resolve endpoints with `IDomainDiscoveryResolver`.
- Build URLs using `InstanceUrlTemplates` and `RemoteOptions.ApiVersion`.
- Use `HttpClient` + resilient policies configured in `AddVNextApiServices`.
- Return `Result` / `ConditionalResult` with error normalization for transport failures.

## Service Discovery

`DomainDiscoveryResolver` resolves endpoints for a target domain:

- Uses `IDistributedCacheService` with ETag-based cache validation.
- Reads `ServiceDiscoveryOptions` for base URL, timeout, retry, and cache TTL.
- Falls back to `RemoteOptions.BaseUrl` when discovery cannot resolve an endpoint.

Endpoint models:

- `DiscoveryEndpoint` (`EndpointKind.Url` or `EndpointKind.Dapr`)
- Cached entries store `BaseUrl`, `DaprAppId`, and `ETag`.

## Domain Registration

`DomainRegistrationService` registers the current domain by starting the
`domain-registration` workflow on the registry runtime.

It sends:

- Domain name (`IRuntimeInfoProvider.Domain`)
- API base URL (`vNextApi:BaseUrl`)
- Health URL (`{baseUrl}/health`)
- Optional Dapr App ID (`DAPR_APP_ID`)

Configuration: `ServiceDiscoveryOptions` (`BaseUrl`, `Domain`, `RegistryFlow`).

## Configuration

`RemoteOptions` (`vNextApi` section):

- `BaseUrl`, `ApiVersion`
- `TimeoutSeconds`, `MaxRetryAttempts`, `RetryDelayMilliseconds`
- `CircuitBreakerFailureThreshold`, `CircuitBreakerTimeoutSeconds`
- `EnableCircuitBreakerBypass`, `InternalOperationHeader`

`ServiceDiscoveryOptions` (`ServiceDiscovery` section):

- `BaseUrl`, `Domain`, `RegistryFlow`
- `TimeoutSeconds`, retry/circuit breaker settings
- `DiscoveryCacheSeconds`, `DiscoveryEndpointTemplate`

## Dependency Injection

`AddInfrastructureModule()` wires:

- `AddDomainDiscovery()` for discovery/registration
- `AddVNextApiServices()` for remote HTTP clients
- `AddInstanceGatewayServices()` for gateway routing

## Implementation References

- `src/BBT.Workflow.Application/Gateway/IInstanceCommandGateway.cs`
- `src/BBT.Workflow.Application/Gateway/IInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RemoteInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RemoteInstanceQueryGateway.cs`
- `src/BBT.Workflow.Application/Discovery/IDomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Application/Discovery/IDomainRegistrationService.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainRegistrationService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceCommandAppService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceQueryAppService.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Extensions/RemoteServiceExtensions.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Configuration/RemoteOptions.cs`
- `src/BBT.Workflow.Application/Discovery/ServiceDiscoveryOptions.cs`
