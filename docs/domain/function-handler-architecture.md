# Function Handler Architecture

## Purpose

Function handlers provide backend-driven view and state APIs behind stable function
routes. They keep controller routing generic while allowing special behavior for system
functions such as state, data, view, schema, authorization, permissions, hierarchy, and
human tasks.

## Boundaries

The `FunctionController` owns route binding. Handler factories select specialized
handlers by function key. Handlers translate HTTP context into application service inputs.
Application services own domain behavior and result construction.

## Architecture Flow

1. Client calls a domain or instance function route.
2. Controller normalizes function key and captures headers/query parameters.
3. Instance handler factory resolves a system handler when available.
4. Handler builds a typed input DTO.
5. Query/application service executes the domain use case.
6. Handler writes response headers such as ETag when required.
7. Unknown function keys fall back to generic function app service handling.

## Contracts

| Function | Handler | Contract notes |
| --- | --- | --- |
| `state` | `StateFunctionHandler` | Supports `If-None-Match`, returns `304`, sets `ETag` and `X-Entity-ETag`. |
| `data` | `DataFunctionHandler` | Supports conditional read and extensions. |
| `view` | `ViewFunctionHandler` | Resolves state or transition view using request headers/query. |
| `schema` | `SchemaFunctionHandler` | Returns transition-aware schema. |
| `authorize` | `AuthorizeFunctionHandler` | Evaluates user/role access. |
| `permissions` | `AuthorizationMatrixFunctionHandler` | Returns authorization matrix. |
| `hierarchy` | `HierarchyFunctionHandler` | Returns instance hierarchy. |
| `humanTask` | `HumanTaskFunctionHandler` | Returns human task state for clients. |

## Failure Modes

- Unknown function falls back to generic function lookup.
- Missing or invalid instance identity returns application-level errors.
- Conditional state/data reads return `304` without response body.
- Authorization-sensitive functions must preserve current-user role and headers.

## Observability

Function calls should preserve request headers, query parameters, domain, workflow,
instance, and role context. ETag behavior is observable through response headers and
client polling status.

## Change Safety

- Add a specialized handler only when generic function behavior is insufficient.
- Keep function type constants stable for clients.
- Do not put domain decision logic in controllers; handlers should translate and delegate.
- Preserve conditional GET behavior for `state` and `data`.

## References

- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Functions/FunctionController.cs`
- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Functions/Handlers/`
- `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs`

