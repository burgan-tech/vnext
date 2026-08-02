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

## Custom Function Contract (verbs, schemas, views)

A custom function declares the contract its clients need in order to call it. Every part is
optional, and a function that declares nothing behaves exactly as it did before this existed.

| Attribute | Purpose |
| --- | --- |
| `verbs[]` | HTTP verbs the function accepts: `GET`, `POST`, `PATCH`, `DELETE`. Empty/absent means no restriction. |
| `inputSchema` | `sys-schemas` reference describing the request body. Enforced at runtime. |
| `outputSchema` | `sys-schemas` reference describing the response body. Declarative only. |
| `inputView` | `sys-views` reference the client renders to collect input. |
| `outputView` | `sys-views` reference the client renders to present output. |

```jsonc
"attributes": {
  "scope": "D",
  "verbs": ["POST"],
  "inputSchema":  { "key": "search-request",  "domain": "core", "flow": "sys-schemas", "version": "1.0.0" },
  "outputSchema": { "key": "search-response", "domain": "core", "flow": "sys-schemas", "version": "1.0.0" },
  "inputView":    { "key": "search-form",     "domain": "core", "flow": "sys-views",   "version": "1.0.0" },
  "task": { }
}
```

### The QUERY verb (deferred, not supported)

The HTTP `QUERY` method — a safe, idempotent read that carries a request body — is **deliberately
not supported**. Declaring it is a component validation error and no route accepts it.

.NET 10 does know the method at the HTTP layer (`HttpMethods.Query`, `HttpMethods.IsQuery`), and
MVC's `HttpMethodAttribute` extension point makes routing it a few lines of work. The blocker is
the surrounding ecosystem, not the runtime: Swagger/OpenAPI generation, gateways, WAFs and client
SDKs do not handle an unrecognised method, so enabling it breaks tooling well before it buys
anything. Revisit when that support lands.

Until then, model body-carrying reads as `POST`.

### Enforcement

Both gates run inside `FunctionAppService.ExecuteFunctionAsync`, after scope enforcement and
authorization and before any task executes. System functions (`state`, `view`, `data`, …) never
reach this path — they are served by their own handlers — so they are unaffected.

| Condition | Result |
| --- | --- |
| `verbs[]` empty or absent | Every routed verb accepted. |
| Verb declared and matched | Request proceeds. |
| Verb declared and not matched | `405 Method Not Allowed` + `Allow` header listing declared verbs. |
| `inputSchema` absent | Body not validated. |
| `inputSchema` set, no body (e.g. `GET`) | Body not validated. |
| `inputSchema` set, body present | Validated via `IJsonSchemaValidator`; failure → `400` with field-level errors. |

Comparison is case-insensitive and verbs are normalized to upper case, so `"post"` and `"POST"`
are equivalent. `outputSchema` is never validated at runtime.

Component validation rejects an unknown verb, a reference pointing at the wrong flow, and an
`inputSchema` declared alongside verbs that can never carry a body (e.g. `verbs: ["GET"]` only),
since the schema would be silently dead.

### Discovery

There is no dedicated contract endpoint. `GET {domain}/functions` already returns each function's
full component definition, including `verbs[]` and the four reference fields, so a client reads the
contract from the definition it already has and resolves the referenced schema/view components the
same way it resolves any other reference.

Note that `GET {domain}/functions/{function}` **invokes** the function — it is not a metadata route.

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

## Query Authorization (queryRoles)

The data-returning instance functions — **state, data, view, schema** — enforce the state's
`queryRoles` (falling back to workflow root `queryRoles`) so a caller may only read an instance
whose current state they are permitted to see.

- Effective grants: the instance's effective-state `queryRoles` when it defines any, otherwise
  `workflow.QueryRoles`. **No grants → allow** (unchanged behavior).
- Grants present: the caller's roles (`ICurrentUser.Roles`, multi-role — any allowed → allow) are
  evaluated via `ITransitionAuthorizationManager.IsQueryAllowedAsync` (DENY wins; predefined
  `$InstanceStarter`/dynamic roles honored). No allow → **HTTP 403**
  (`Error.Forbidden(WorkflowErrorCodes.AuthorizationRoleDenied)` → mapped in `AddExceptionHandling`).
- Because all four functions share the same gate, denying `state` also denies `data`/`view`/`schema`
  — the client cannot see data, view, or schema for a state it is not authorized to query.
- The `authorize` function (`checkQueryRoles=true`) shares the same core evaluator
  (`AuthorizeAppService.EvaluateQueryRolesAsync` delegates to `IsQueryAllowedAsync`).

### Custom function Roles

**Custom (user-defined) functions** additionally enforce their own `Function.Roles`: when a custom
function declares `roles`, the caller's roles are evaluated via
`ITransitionAuthorizationManager.IsAnyRoleAllowedForGrantsAsync` at the single execution chokepoint
(`FunctionAppService.ExecuteFunctionAsync`, covering both instance- and domain-scoped custom
functions). No allow → **HTTP 403** (`WorkflowErrors.FunctionAccessDenied`); no `roles` → allow.
Built-in functions (state/data/view/schema/authorize/permissions/hierarchy/extensions, `human-task`)
are **excluded** — they use their own handlers and never flow through `FunctionAppService`.

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
- `src/BBT.Workflow.Application/Authorization/ITransitionAuthorizationManager.cs` (`IsQueryAllowedAsync` — queryRoles gate)

