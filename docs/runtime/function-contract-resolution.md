# Function Contract Resolution

A function's four contract slots — `inputSchema`, `outputSchema`, `inputView`, `outputView` — are
**rule-based**. Each can be authored as a single component reference or as a list of entries, where
the runtime picks one per request by evaluating rules in declaration order.

This is the same mechanism state and transition views already use (`views[]`, first matching
`IConditionMapping` rule wins), applied to functions so a single-page "mini flow" function can serve
a different form or a different payload contract depending on the caller, channel or instance data.

## Wire shapes

All three forms are accepted on every slot and produce the same in-memory model:

```jsonc
// 1. Single reference — the common case.
"inputView": { "key": "search-form", "domain": "core", "flow": "sys-views", "version": "1.0.0" }

// 2. Entry array.
"inputView": [
  { "rule": { "location": "./mobile.csx", "code": "...", "encoding": "B64" },
    "view": { "key": "search-form-mobile", "domain": "core", "flow": "sys-views", "version": "1.0.0" },
    "loadData": true },
  { "view": { "key": "search-form", "domain": "core", "flow": "sys-views", "version": "1.0.0" } }
]

// 3. Wrapped array — what the runtime always writes back.
"inputView": { "views": [ { "view": { "...": "..." } } ] }
```

Schema slots use `schema` instead of `view`, and `{ "schemas": [...] }` as the wrapper. A schema
entry carries no `loadData`; a function view entry may **not** carry `extensions` (there is no data
function to apply them to — the component validator rejects it).

Conversion is handled by `ViewDefinitionJsonConverter` and `SchemaSelectionJsonConverter`
(`src/BBT.Workflow.Domain/Shared/`), which always serialize the canonical wrapped form.

> The type behind a schema slot is `SchemaSelection`, not `SchemaDefinition` — the latter is already
> the `sys-schemas` component entity.

## Resolution rules

`FunctionContractResolver` (`src/BBT.Workflow.Application/Functions/Contracts/`) evaluates one slot:

| Case | Outcome |
| --- | --- |
| Slot not declared, or declares no entries | No contract (`Ok(null)`) |
| Entry has no `rule` | Wins immediately — it is the declared fallback |
| Entry's rule evaluates true | Wins |
| Entry's rule evaluates false | Skip to the next entry |
| Entry's rule fails to evaluate | Logged at Warning, entry skipped — never fatal |
| Every entry carried a rule and none matched | No contract (`Ok(null)`) — **not** an error |
| Script context could not be built | Failure |

**No contract is not an error.** A function may legitimately have no applicable view for a given
request. Callers decide what that means: request validation treats it as "nothing to validate", the
info endpoint reports `hasView: false`, and the content routes return `404`
(`Function:800004`).

Rules are compiled and executed through `ITaskConditionService` — the same path state and transition
view rules take. Contract resolution never compiles scripts itself.

### Authoring constraint

At most one rule-less entry per slot, and it must be **last**. A rule-less entry always matches, so
anything after it is unreachable; `FunctionComponentValidator` rejects that at definition time
rather than letting it silently never fire.

## Script context

Rules are evaluated against a `ScriptContext` that exposes `Headers`, `QueryParameters`, `Instance`
and `Body`, built through `LazyScriptContext`:

- Built **at most once** per request and shared across all four slots, so a rule-based function is
  evaluated against one consistent snapshot.
- Built **only if** some entry actually declares a rule. Building a context serializes the instance's
  full latest data, and the overwhelming majority of functions declare no rules — they must not pay
  for it.

On the **execute** path the context's body is the request body, and the same instance is reused for
the cache key expression and the tasks. On the **discovery** path (`/info`, `/view`, `/schema`) there
is no request body, so rules see the instance's latest data as the body — the same material state
and transition view rules read.

### Ordering note

When `inputSchema` is rule-based, the script context is now built *before* schema validation rather
than after. The build is read-only, and for any definition that declares no rules the ordering is
unchanged.

## Where it is used

| Surface | Slot | Behaviour on no match |
| --- | --- | --- |
| `FunctionRequestValidationService` (execute) | `inputSchema` | Body is not validated |
| `/info` | all four | `hasView` / `hasSchema` false, href still emitted |
| `/view?target=input\|output` | `inputView` / `outputView` | `404` |
| `/schema?target=input\|output` | `inputSchema` / `outputSchema` | `404` |

`outputSchema` is never enforced at runtime — it is declarative, surfaced for clients and tooling.

## Access control

Every surface above runs `IFunctionAccessPolicy` — the shared scope and role gate — before it
resolves or reveals anything. Execution and discovery pass through the *same* object precisely so
they cannot drift: a caller who cannot invoke the function cannot read its contracts either.

See [Function handler architecture](../domain/function-handler-architecture.md) for the endpoint
surface and [Role grant authorization](../domain/role-grant-authorization.md) for how `roles` are
evaluated.
