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
- Build URLs using **static `InstanceUrlTemplates`** and `RemoteOptions.ApiVersion`.
- Use `HttpClient` + resilient policies configured in `AddVNextApiServices`.
- Return `Result` / `ConditionalResult` with error normalization for transport failures.

**Note:** Remote clients use static URL templates because they call internal controller routes that must remain fixed.

## Client-Facing URL Templates (HATEOAS)

Controllers in the Orchestration API generate HATEOAS links using **configurable URL templates** to support gateway routing patterns.

### URL Template Builder

`IUrlTemplateBuilder` (implemented by `UrlTemplateBuilder`) generates client-facing URLs from configured templates:

- Used by `InstanceController` and `FunctionController` for HATEOAS link generation
- Configured via `UrlTemplateOptions` in `appsettings.json`
- Supports gateway prefixes and custom routing patterns
- Independent from internal service-to-service URLs

### Configuration

`UrlTemplateOptions` (`UrlTemplates` section) configures client-facing URL patterns:

```json
{
  "UrlTemplates": {
    "Start": "/{0}/workflows/{1}/instances/start",
    "Transition": "/{0}/workflows/{1}/instances/{2}/transitions/{3}",
    "FunctionList": "/{0}/workflows/{1}/functions/{2}",
    "InstanceList": "/{0}/workflows/{1}/instances",
    "Instance": "/{0}/workflows/{1}/instances/{2}",
    "InstanceHistory": "/{0}/workflows/{1}/instances/{2}/transitions"
  }
}
```

Template parameters:
- `{0}` = domain
- `{1}` = workflow
- `{2}` = instance/instanceId
- `{3}` = transitionKey/function

### Gateway Routing Example

For API Gateway with `/api/gateway` prefix:

```json
{
  "UrlTemplates": {
    "Start": "/api/gateway/{0}/workflows/{1}/instances/start",
    "Transition": "/api/gateway/{0}/workflows/{1}/instances/{2}/transitions/{3}",
    "FunctionList": "/api/gateway/{0}/workflows/{1}/functions/{2}",
    "InstanceList": "/api/gateway/{0}/workflows/{1}/instances",
    "Instance": "/api/gateway/{0}/workflows/{1}/instances/{2}",
    "InstanceHistory": "/api/gateway/{0}/workflows/{1}/instances/{2}/transitions"
  }
}
```

### URL Types Comparison

| URL Type | Purpose | Configuration | Used By |
|----------|---------|---------------|---------|
| Client-facing | HATEOAS links in responses | `UrlTemplates` (configurable) | Controllers |
| Internal | Service-to-service calls | `InstanceUrlTemplates` (static) | Remote infrastructure |

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

`UrlTemplateOptions` (`UrlTemplates` section) - **Orchestration API only**:

- Configures client-facing URL patterns for HATEOAS responses
- Supports gateway routing and custom URL patterns
- See "Client-Facing URL Templates" section above for details

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

### Gateway Routing
- `src/BBT.Workflow.Application/Gateway/IInstanceCommandGateway.cs`
- `src/BBT.Workflow.Application/Gateway/IInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RoutedInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceQueryGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RemoteInstanceCommandGateway.cs`
- `src/BBT.Workflow.Infrastructure/Gateway/RemoteInstanceQueryGateway.cs`

### Service Discovery
- `src/BBT.Workflow.Application/Discovery/IDomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainDiscoveryResolver.cs`
- `src/BBT.Workflow.Application/Discovery/IDomainRegistrationService.cs`
- `src/BBT.Workflow.Infrastructure/Discovery/DomainRegistrationService.cs`
- `src/BBT.Workflow.Application/Discovery/ServiceDiscoveryOptions.cs`

### Remote Clients
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceCommandAppService.cs`
- `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceQueryAppService.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Extensions/RemoteServiceExtensions.cs`
- `src/BBT.Workflow.Infrastructure/Remote/Configuration/RemoteOptions.cs`

### URL Templates
- `src/BBT.Workflow.Domain/Definitions/UrlTemplateOptions.cs` - Configuration model
- `src/BBT.Workflow.Domain/Definitions/IUrlTemplateBuilder.cs` - Builder interface
- `src/BBT.Workflow.Infrastructure/Definitions/UrlTemplateBuilder.cs` - Implementation
- `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs` - Static templates for internal use
