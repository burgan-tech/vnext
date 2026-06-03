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

## State Alias (Role-Based State Visibility)

A state may declare an `alias` array so the same internal state is presented under
different, role-appropriate labels without changing the workflow's real state identity.
This lets internal evaluation states (fraud check, KPS, limit checks, …) stay hidden from
the end customer while back-office actors see a more detailed label.

```json
{
  "key": "fraud-check",
  "stateType": "Intermediate",
  "alias": [
    {
      "name": "Operasyon İncelemesinde",
      "roles": [ { "role": "backoffice.operator", "grant": "allow" } ],
      "labels": [
        { "label": "Operasyon İncelemesinde", "language": "tr" },
        { "label": "Under Operational Review", "language": "en" }
      ]
    },
    { "name": "Değerlendirme Aşamasında", "roles": [] }
  ]
}
```

Resolution in the `state` function:

- When the current (main-flow) state defines aliases, `StateFunctionHandler` →
  `InstanceQueryAppService.BuildInstanceStateOutputAsync` resolves them using the same
  role evaluator as transition filtering (`ITransitionAuthorizationManager.IsRoleAllowedForGrantsAsync`):
  static roles, predefined roles (`$InstanceStarter`, `$PreviousUser`, …) and dynamic roles.
- Aliases are evaluated in declaration order; the **first** alias whose `roles` resolve to
  the caller wins.
- Authoring rules (enforced by `WorkflowValidator`): the `alias` array is optional, but each
  entry must declare a `name`, at least one `roles` grant, and at least one `labels` entry.
- The winning alias's display value for the response `state` field is resolved as:
  1. **Localized label** — if the alias has `labels`, the label for the caller's current
     language is returned. Language comes from the `Accept-Language` header (`LanguageResolver`);
     match order is exact culture (`tr-TR`) → neutral language (`tr`, incl. `tr-*`) →
     English (`en-US`/`en`) → first label (`LanguageLabelExtensions.ResolveLabel`).
  2. **Alias `name`** — when the alias has no `labels`.
  3. **Raw state key** — when the state defines no aliases, or none resolves for the caller
     (current behavior).
- Only the `state` representation changes; `stateType`, status, transitions, and all internal
  workflow logic continue to use the real state identity (`instance.CurrentState`). Because the
  resolved value participates in the representation, different roles/languages get different `ETag`s.
- Aliasing applies only to the main-flow current state. While a non-terminal subflow is
  borrowing the displayed state, the value is left untouched.
- `ICurrentLanguage` (registered scoped, `HttpContext`-based) is the reusable per-request handle
  for the same culture resolution; the state function resolves from the request headers it already
  receives so it stays correct under forwarded/subflow contexts.

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
- `src/BBT.Workflow.Domain/Definitions/States/StateAlias.cs` (state alias model + localized labels)
- `src/BBT.Workflow.Application/Instances/InstanceQueryAppService.cs` (`ResolveStateAliasDisplayAsync`)
- `src/BBT.Workflow.Domain/Localization/LanguageResolver.cs` (`Accept-Language` → culture)
- `src/BBT.Workflow.Domain/Shared/LanguageLabelExtensions.cs` (`ResolveLabel` fallback chain)
- `src/BBT.Workflow.Application/Languages/ICurrentLanguage.cs` + `src/BBT.Workflow.HttpApi.Shared/Services/Languages/HttpContextCurrentLanguage.cs`

