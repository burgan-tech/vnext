# Declarative Task Mappings

A task binding (`onExecuteTask` entry of a state `onEntries`/`onExits`, a transition `onExecutionTasks`, a function `task` / `onExecutionTasks`, an extension `task`) can shape its request and write its result without a Roslyn `.csx` mapping. The component contract is extended compatibly: no new format version, every existing binding keeps its meaning. Code: `DeclarativeTaskMappingRunner`, `MappingBlockValidator`, `ExpressionScope`, `JsonPath`.

## Contract

```json
{
  "order": 1,
  "task": { "key": "score-task", "domain": "core", "version": "1.0.0", "flow": "sys-tasks" },
  "input": {
    "set": { "$.body.customerId": "data.customer.id", "$.headers.x-channel": "\"web\"" }
  },
  "output": {
    "select": "$.body.score",
    "into": "$.kyc.score",
    "set": { "$.kyc.checkedAt": "now", "$.kyc.state": "instance.currentState" }
  }
}
```

- `input` and `output` are optional; each holds `select`, `into` and `set` (path to Dynamic Expresso expression, max 8000 characters each).
- **`mapping` and the blocks are mutually exclusive per binding** — publish error if both are present. A binding stored with both before this validation existed keeps its legacy meaning: the script runs and the blocks are ignored.
- A `mapping` with `location: "dynamicExpresso"` is a **publish warning**, not an error: the script engine never reads a mapping's `location`, so such a mapping runs as a C# `IMapping` script, as it always has (an empty one is a no-op). The warning reads `<location>.mapping: The dynamicExpresso location is ignored on a task 'mapping': the mapping runs as a C# script. Author the declarative 'input' and 'output' blocks for expression-based mappings.` Behaviour is unchanged; the label is deprecated (`task-mapping-dynamic-expresso-location` in `vnext-meta/unreleased/declarative-task-mappings.json`).
- **Assignment is disabled** in mapping expressions (Dynamic Expresso `AssignmentOperators.None` on the mapping signatures): `data.x = 1` is a compile error at publish (`MappingBlockValidator`) and fails the mapping at run time. An expression produces a value; only `into` and `set` keys write. The legacy condition, cache-key and agent-tool expressions are unchanged.
- Paths use one in-house subset: `$`, `.name` (hyphens allowed), `['name']`, `[n]` and `[*]` (read only). No filters, no recursive descent. A write target (`into`, `set` key) must be writable: no index, no wildcard. An array is written whole (replace), like the full-merge model.

## Variables

| Variable | Meaning |
|---|---|
| `data` | the instance's latest data |
| `input` | the transition request body (never the task payload; no fallback) |
| `instance` | read-only instance view, camelCase members: `id`, `key`, `flow`, `version`, `currentState`, `effectiveState`, `effectiveStateType`, `effectiveStateSubType`, `effectiveStatus`, `type` (`R`/`S`/`P`), `data` |
| `now` | one timestamp per mapping |
| `user` | `available` (bool), `roles` (string[]), `hasRole(string)` — see [User](#user) |
| `context` | the legacy rule context, unchanged (still fails on a missing member) |
| `result` | **output phase only**: the task's standard response `body`, `headers`, `statusCode` |

Variables are bound lazily, only when an expression uses them; nothing is cloned.

**Member names are case-sensitive and camelCase everywhere a mapping author writes them**: JSON members of `data`, `input` and `result` as stored (`data.customer.id`), the `instance` view (`instance.currentState`) and `user` (`user.hasRole("x")`). `instance.CurrentState` is a compile error at publish. Only the legacy `context` keeps its PascalCase members (`context.Instance.CurrentState`, `context.Body`), unchanged. JSON values need a conversion for comparison and arithmetic — `data.amount.AsDouble() > 1000`, `AsInt32()`, `AsBoolean()`, `AsArrayLength()` — but not to be written as they are.

## Input phase

- Targets start with a request surface: `$.body`, `$.headers`, `$.query`. Each starts from the task's configured value and is written through the task type's own setter. `$.route` is rejected at publish (no task type supports it yet).
- `select` + `into` copies one value; they must appear together (publish error otherwise). An input `select` starts with a variable name (`$.data.customer`).
- Surfaces by task type (the `IHas*` interfaces; publish cannot see the task type, so an unsupported surface fails at run time):

| Task type | body | headers | query |
|---|---|---|---|
| `HttpTask`, `DirectTriggerTask`, `StartTask`, `SubProcessTask` | yes | yes | no |
| `DaprServiceTask` | yes | yes | yes |
| `DaprHttpEndpointTask`, `DaprBindingTask`, `DaprPubSubTask` | yes | no | no |
| `SoapTask`, `GetInstanceTask`, `GetInstanceDataTask`, `GetInstancesTask` | no | yes | no |
| any other task type | no | no | no |

- Query round-trip: the query string is parsed to pairs and re-encoded, so repeated keys collapse and `+` is re-encoded.
- The input phase journals nothing (the values are not echoed into the task journal).
- **FanOut, Notification and Human tasks reject a binding that carries declarative blocks at run time** (`TaskDeclarativeMappingFailed`). Script, Python, MCP and CacheAside tasks run output blocks; their input has no surface.

## Output phase

- `select` reads `result` (root `$` is `result`; default `$.body`); `into` and `set` target instance data (not the root `$`).
- Without `into`, the selected object merges at the data root, like an `OutputHandler` returning `Data`. Without `select`, `into` takes `result.body`.
- **`result` is read in place, not rebuilt.** `body` is the task's response `Data` as it is (a `JsonElement`, as the HTTP and Dapr invokers return it), else that `Data` serialized once with the runtime's JSON options; a `select` or `result.<member>` walks only that member; the whole `{ body, headers, statusCode }` object is written out only when `result` itself is used as a value (`select: "$"`, `set: { "$.x": "result" }`). Every value written to the delta is a subtree-sized copy, so the delta never keeps the task response alive.
- **Deliberate difference — deep response bodies.** Before the in-place read, the runner re-serialized the whole response through default JSON options (depth limit 64), so a body nested deeper than about 63 levels failed the mapping even when the `select` / `set` touched only a shallow part. Now a `select` or member read on such a body succeeds; the limits that remain are 64 levels for the value copied into the delta and for materializing the whole `result`, and 256 levels (the runtime's `JsonSerializerConstants.JsonOptions.MaxDepth`) for serializing a non-JSON `Data`.
- **Ordering:** `into` first, then `set` entries in declaration order (a later entry wins on a collision). One delta object results in **one** instance-data append, so x-encryption, versioning and ETag are inherited. Encrypted fields read in an expression are the stored tokens and stay tokens when written back. An empty delta writes no version.

## Missing values

A missing `select` path, a missing leaf, `?.`, or member access on a missing intermediate (`data.customer.id` with no `customer`) makes that entry **write nothing** — no error. A skipped `select` (path not found) or `set` entry (missing intermediate) raises the Debug log `TaskDeclarativeMappingEntrySkipped` (EventId 10269) with the task key, the phase and the path or `set` key — never the value; a plain null value or `?.` result skips silently. Every other evaluation error (bad cast, unwritable path, role provider failure) **fails the mapping**: the error boundary applies and `TaskDeclarativeMappingFailed` is logged. Inconsistency to know: legacy `context.*` paths still fail on a missing member.

## User

`user` is available in task mappings on **caller-driven (User) hops** of state `onEntries` / `onExits` and transition `onExecutionTasks`. On System hops (fired timers, SubFlow resume, continuations) `user.available` is `false` and reading `user.roles` / `user.hasRole` — in a `set` expression or through an input `select` such as `$.user.roles` — fails the mapping (fail-closed, never an empty set). That failure is **deterministic, not transient**: a `TaskExecution` failure whose message says to guard the read with `user.available`, never `Authorization:110006`. `110006` (transient, retryable) is kept for a real role-provider failure while resolving the caller's roles. An input `select: "$.user.available"` reads the flag on any hop. Functions and extensions always see `user.available == false`. Roles are resolved once, and only when an expression uses them. Script mappings never see the caller. Provider and surfaces: [Script Caller Roles](script-caller-roles.md).

**Persisting roles is the author's responsibility.** An output `set` such as `{ "$.approval.roles": "user.roles" }` writes the caller's roles into instance data, where every later reader of that data (state and data functions, scripts, other callers allowed to read the instance) sees them. Write roles into data only when that exposure is intended, and protect the field (`x-roles`, `x-masking`) as you would any other caller-derived value.

## Publish validation

`MappingBlockValidator` parses every path and compiles every `set` expression (warming the cache). Error locations are camelCase JSON paths, for example `states[review].onEntries[0].output.set['$.kyc.score']: ...`, unlike older validators' PascalCase member paths.
