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
| `catalog` | `CatalogFunctionHandler` | Lists the workflow's declared functions, role-filtered, each linked to its `info` endpoint. |

## Custom Function Contract (verbs, schemas, views)

A custom function declares the contract its clients need in order to call it. Every part is
optional, and a function that declares nothing behaves exactly as it did before this existed.

| Attribute | Purpose |
| --- | --- |
| `verbs[]` | HTTP verbs the function accepts: `GET`, `POST`, `PATCH`, `DELETE`. Empty/absent means no restriction. |
| `inputSchema` | `sys-schemas` contract describing the request body. Enforced at runtime. |
| `outputSchema` | `sys-schemas` contract describing the response body. Declarative only. |
| `inputView` | `sys-views` contract the client renders to collect input. |
| `outputView` | `sys-views` contract the client renders to present output. |

Each of the four slots is **rule-based**: author it as a single component reference, or as entries
evaluated in declaration order where the first match wins. See
[Function contract resolution](../runtime/function-contract-resolution.md) for the full rules.

```jsonc
"attributes": {
  "scope": "D",
  "verbs": ["POST"],
  // Single reference - the common case.
  "inputSchema":  { "key": "search-request",  "domain": "core", "flow": "sys-schemas", "version": "1.0.0" },
  "outputSchema": { "key": "search-response", "domain": "core", "flow": "sys-schemas", "version": "1.0.0" },
  // Rule-based - first match wins, the trailing rule-less entry is the fallback.
  "inputView": [
    {
      "rule": { "location": "./mobile-rule.csx", "code": "...", "encoding": "B64" },
      "view": { "key": "search-form-mobile", "domain": "core", "flow": "sys-views", "version": "1.0.0" }
    },
    { "view": { "key": "search-form", "domain": "core", "flow": "sys-views", "version": "1.0.0" } }
  ],
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
| `inputSchema` set, no body (e.g. `GET`) | Body not validated - checked before any rule runs. |
| `inputSchema` rule-based, no entry matched | Body not validated; no contract applies to this request. |
| `inputSchema` resolved, body present | Validated via `IJsonSchemaValidator`; failure → `400` with field-level errors. |

Comparison is case-insensitive and verbs are normalized to upper case, so `"post"` and `"POST"`
are equivalent. `outputSchema` is never validated at runtime.

Component validation rejects an unknown verb, a reference pointing at the wrong flow, and an
`inputSchema` declared alongside verbs that can never carry a body (e.g. `verbs: ["GET"]` only),
since the schema would be silently dead. For rule-based slots it additionally rejects a rule-less
entry that is not last (everything after it is unreachable) and an `extensions` declaration on a
function view entry (there is no data function to apply extensions to).

### Discovery

`GET .../functions/{function}/info` describes a function to a client that is about to call it: may I
run this, with which verb, at which URL, and which view and schema apply *right now*. The response
follows the state function's hyperlink style — the client follows hrefs rather than resolving
component references itself.

```
GET {domain}/functions/{function}/info
GET {domain}/workflows/{workflow}/instances/{instance}/functions/{function}/info
```

```jsonc
{
  "key": "customer-search",
  "domain": "core",
  "version": "1.0.0",
  "scope": "D",
  "rawResponse": false,
  "cacheable": true,
  "function": { "href": "/core/functions/customer-search", "verbs": ["POST"] },
  "inputView":    { "href": "/core/functions/customer-search/view?target=input",    "hasView": true, "loadData": false },
  "outputView":   { "href": "/core/functions/customer-search/view?target=output",   "hasView": false, "loadData": false },
  "inputSchema":  { "href": "/core/functions/customer-search/schema?target=input",  "hasSchema": true },
  "outputSchema": { "href": "/core/functions/customer-search/schema?target=output", "hasSchema": true }
}
```

The `has*` flags say whether following the href *right now* returns content. The href is emitted
either way, because a rule reads request state and can match on a later call.

The hrefs point at four content routes, which re-evaluate the rules on every call:

```
GET {domain}/functions/{function}/view?target=input|output
GET {domain}/functions/{function}/schema?target=input|output
GET {domain}/workflows/{workflow}/instances/{instance}/functions/{function}/view?target=input|output
GET {domain}/workflows/{workflow}/instances/{instance}/functions/{function}/schema?target=input|output
```

`target` defaults to `input`. The view route returns the same payload as the state-level `view`
function, so clients reuse their existing rendering path.

All six routes run the **same** scope and role gates as execution
(`IFunctionAccessPolicy`), so a caller who could not invoke the function cannot learn its shape
either: a denied caller gets `403`, not an empty description. A slot that resolves to nothing on a
content route is `404` (`Function:800004`); an unrecognized `target` is `400` (`Function:800005`).

**Built-in system functions are not describable.** `state`, `view`, `data`, `schema`, `authorize`,
`permissions`, `hierarchy`, `human-task`, `master` and `catalog` have no `sys-functions` component, so
`/info` returns `404` for them.

`GET {domain}/functions` still returns each function's full component definition for tooling that
wants the raw shape. Note that `GET {domain}/functions/{function}` **invokes** the function — it is
not a metadata route.

### The `catalog` function

A client polling an instance does not need to know in advance which functions exist. The state
response points at the catalog:

```jsonc
"functions": {
  "hasFunctions": true,
  "href": "/core/workflows/onboarding/instances/{id}/functions/catalog"
}
```

`hasFunctions` is `workflow.Functions.Count > 0` — free to compute. The href is always emitted; the
flag lets a client skip the call entirely when the flow ships no functions.

Following it returns the list:

```jsonc
GET {domain}/workflows/{workflow}/instances/{instance}/functions/catalog

{
  "functions": [
    { "name": "get-branches", "version": "1.0.0", "scope": "D",
      "href": "/core/functions/get-branches/info" },
    { "name": "calc-limit", "version": "1.0.0", "scope": "F",
      "href": "/core/workflows/onboarding/instances/{id}/functions/calc-limit/info" }
  ]
}
```

Each href matches the function's `scope` — `D` gets the domain route, `F` and `I` the instance route,
because the domain route rejects those two with `403`. `scope` travels alongside so the client can
branch on it without inferring anything from the URL. Declaration order is preserved.

**Role-filtered**, through the same `IFunctionAccessPolicy` as execution and `/info`: a function the
caller could not invoke is not advertised, so every link handed out is actionable. A reference whose
component cannot be resolved is logged (`WorkflowFunctionReferenceUnresolved`) and omitted rather than
failing the catalog.

#### Why the list is not inlined in the state response

Resolving it costs one component read per declared function *plus* a role evaluation each. The state
response is served on every long-poll, so that work does not belong there — and it is wasted anyway
for the many clients that never look at functions. Behind `catalog` it is paid once, on demand.
`InstanceQueryAppServiceStateTests.GetInstanceStateAsync_NeverReadsFunctionComponents` pins that the
state path reads none.

`functions` does **not** participate in the state ETag: `hasFunctions` is a property of the flow
version, which the fingerprint already covers, so it cannot change while an instance is parked. The
`v4` `ResponseShapeVersion` bump is what made existing clients observe the new shape. See
[Instance Function Cache and Fingerprint ETag](../runtime/state-function-cache-and-etag.md).

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

