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
- **`mapping` and the blocks are mutually exclusive per binding** — publish error if both are present.
- A `mapping` with `location: "dynamicExpresso"` is a **publish error** (one expression cannot be both input and output handler). Before, it passed publish and failed at run time as a Roslyn compile error. Use the blocks. Recorded in `vnext-meta/unreleased/declarative-task-mappings.json`.
- Paths use one in-house subset: `$`, `.name` (hyphens allowed), `['name']`, `[n]` and `[*]` (read only). No filters, no recursive descent. A write target (`into`, `set` key) must be writable: no index, no wildcard. An array is written whole (replace), like the full-merge model.

## Variables

| Variable | Meaning |
|---|---|
| `data` | the instance's latest data |
| `input` | the transition request body (never the task payload; no fallback) |
| `instance` | read-only instance view (for example `instance.currentState`) |
| `now` | one timestamp per mapping |
| `user` | `available` (bool), `roles` (string[]), `hasRole(string)` — see [User](#user) |
| `context` | the legacy rule context, unchanged (still fails on a missing member) |
| `result` | **output phase only**: the task's standard response `body`, `headers`, `statusCode` |

Variables are bound lazily, only when an expression uses them; nothing is cloned.

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
- **Ordering:** `into` first, then `set` entries in declaration order (a later entry wins on a collision). One delta object results in **one** instance-data append, so x-encryption, versioning and ETag are inherited. Encrypted fields read in an expression are the stored tokens and stay tokens when written back. An empty delta writes no version.

## Missing values

A missing `select` path, a missing leaf, `?.`, or member access on a missing intermediate (`data.customer.id` with no `customer`) makes that entry **write nothing** — no error, no log. Every other evaluation error (bad cast, unwritable path, role provider failure) **fails the mapping**: the error boundary applies and `TaskDeclarativeMappingFailed` is logged. Inconsistency to know: legacy `context.*` paths still fail on a missing member.

## User

`user` is available in task mappings on **caller-driven (User) hops** of state `onEntries` / `onExits` and transition `onExecutionTasks`. On System hops (fired timers, SubFlow resume, continuations) `user.available` is `false` and reading `user.roles` / `user.hasRole` fails the mapping (fail-closed, never an empty set). Functions and extensions always see `user.available == false`. Roles are resolved once, and only when an expression uses them. Script mappings never see the caller. Provider and surfaces: [Script Caller Roles](script-caller-roles.md).

## Publish validation

`MappingBlockValidator` parses every path and compiles every `set` expression (warming the cache). Error locations are camelCase JSON paths, for example `states[review].onEntries[0].output.set['$.kyc.score']: ...`, unlike older validators' PascalCase member paths.
