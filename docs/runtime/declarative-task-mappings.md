# Declarative Task Mappings

A task binding (`onExecuteTask` entry of a state `onEntries`/`onExits`, a transition `onExecutionTasks`, a function `task` / `onExecutionTasks`, an extension `task`) can shape its request and write its result without a Roslyn `.csx` mapping. The component contract is extended compatibly: no new format version, every existing binding keeps its meaning. Code: `DeclarativeTaskMappingRunner`, `MappingBlockValidator`, `ExpressionScope`, `JsonPath`. The same blocks also shape a transition's result and a SubFlow's start request and result: see [Blocks outside task bindings](#blocks-outside-task-bindings).

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
- **Assignment is disabled** in mapping expressions (Dynamic Expresso `AssignmentOperators.None` on the mapping signatures): `data.x = 1` is a compile error at publish (`MappingBlockValidator`) and fails the mapping at run time. An expression produces a value; only `into` and `set` keys write. Condition rules and string keys are assignment-off too (see [Dynamic Expresso outside task mappings](#dynamic-expresso-outside-task-mappings)); the agent-tool `binding.code` expressions are unchanged.
- Paths use one in-house subset: `$`, `.name` (hyphens allowed), `['name']`, `[n]` and `[*]` (read only). No filters, no recursive descent. A write target (`into`, `set` key) must be writable: no index, no wildcard. An array is written whole (replace), like the full-merge model.

## Variables

| Variable | Meaning |
|---|---|
| `data` | the instance's latest data |
| `input` | the raw request payload of the hop — the transition request body (never the task payload, never `context.Body`); see [`input` per trigger](#input-per-trigger) |
| `instance` | read-only instance view, camelCase members: `id`, `key`, `flow`, `version`, `currentState`, `effectiveState`, `effectiveStateType`, `effectiveStateSubType`, `effectiveStatus`, `type` (`R`/`S`/`P`), `data` |
| `now` | one timestamp per mapping |
| `user` | `available` (bool), `roles` (string[]), `hasRole(string)` — see [User](#user) |
| `context` | the legacy rule context, unchanged (still fails on a missing member) |
| `task` | the task's `config` as authored (a JSON view like `data`): `task.url`, `task.body.x`; in the output phase, as the input phase left it |
| `config` | the host configuration: `config.get("Key")`, `config.get("Key", "fallback")` (same reach as `GetConfigValue`) |
| `result` | **output phase only**: the task's standard response `body`, `headers`, `statusCode` |
| `headers` | the **incoming request's** headers, read-only: `headers["x-channel"]` (the value, or `null` when absent), `headers.has("x-channel")` (bool); names are case-insensitive — see [Headers](#headers) |

### Headers

`headers` is a thin view over the same dictionary that backs `context.Headers` — nothing is copied or serialized, and
it is bound only when an expression uses it. It therefore follows `Workflow:HeaderExposure:Deny` exactly as
`context.Headers` does: a denied name is absent from both ([Header Exposure](header-exposure.md)). The runtime's own
transport headers (the unreduced request the runtime uses for credentials and identity) are never reachable.

- **Use the indexer for a hyphenated name**: `headers["x-channel"]`. `headers.x-channel` parses as a subtraction and
  does not compile.
- Two members only: the indexer and `has(name)`. A value is text (`headers["x-n"]` is `"5"`, not a number).
- **`task.headers` is not `headers`.** In a task mapping, `task.headers` is the task's own configured **outgoing**
  headers (its `config.headers`, what the task will send, written with `"$.headers.x-channel"` targets); `headers` is the
  **incoming** request's headers. Forwarding one: `"set": { "$.headers.x-channel": "headers[\"x-channel\"]" }`.
- `headers` is not a `select` root: `select` reads JSON variables only, so `"select": "$.headers.x"` is a publish error.
  Read a header with a `set` expression instead: `"$.body.channel": "headers[\"x-channel\"]"`.
- `context.Headers` is unchanged and keeps working.

Function: `urlEncode(value)` percent-encodes one URL component. `task`, `config` and `urlEncode` exist in task mappings only, not in condition rules. `headers` exists in every slot below except the agent tool `binding.code`.

Variables are bound lazily, only when an expression uses them; nothing is cloned.

**Member names are case-sensitive and camelCase everywhere a mapping author writes them**: JSON members of `data`, `input` and `result` as stored (`data.customer.id`), the `instance` view (`instance.currentState`) and `user` (`user.hasRole("x")`). `instance.CurrentState` is a compile error at publish. Only the legacy `context` keeps its PascalCase members (`context.Instance.CurrentState`, `context.Body`), unchanged. JSON values need a conversion for comparison and arithmetic — `data.amount.AsDouble() > 1000`, `AsInt32()`, `AsBoolean()`, `AsArrayLength()` — but not to be written as they are.

## Input phase

- **Targets are fields of the task's own `config`** — the object authored in the task definition — so the names are the task JSON's: `$.url`, `$.method`, `$.body.customer`, `$.headers.x-channel`, `$.timeoutSeconds`, a DaprService's `$.queryString`, a DaprBinding's `$.data`. Anything a C# `InputHandler` could change through a setter is reachable; no task type needs a per-field contract.
- The block edits a **copy** of the configured `config` (fields it does not touch keep their configured value) and hands the result back through the task type's own `Configure` parser (`WorkflowTask.ApplyConfig`), exactly as if the task definition had carried it. A value that parser rejects (`"$.url": "42"`) fails the mapping.
- A path INTO a field whose configured value is not an object (`$.body.x` over an array body) fails instead of replacing it; target the field as a whole to replace it. An absent or `null` field becomes an object.
- A `headers` object's values are written as strings (`"$.headers.x-count": "3"` sends `"3"`).
- **A field the task type does not read is ignored**, as it would be in the task definition: a binding holds only a task reference, so publish cannot see the type, and the field set is the type's parser. Check spelling against the task's schema.
- `select` + `into` copies one value; they must appear together (publish error otherwise). An input `select` starts with a JSON variable name (`$.data.customer`, `$.task.body`); `config` and `headers` are not selectable (read a header with a `set` expression, `headers["x-channel"]`).
- The input phase journals nothing (the values are not echoed into the task journal).
- **FanOut, Notification and Human tasks reject a binding that carries declarative blocks at run time** (`TaskDeclarativeMappingFailed`). Every other task type runs both blocks.

### Several headers

One `set` key per header. With the task configured as
`"headers": { "Content-Type": "application/json", "x-channel": "mobile" }`:

```json
"input": {
  "set": {
    "$.headers.x-channel": "\"web\"",
    "$.headers.x-customer-id": "data.customer.id",
    "$.headers.x-request-id": "instance.id",
    "$.headers.x-amount": "data.amount",
    "$.headers['x-correlation.id']": "instance.key",
    "$.headers.Accept-Language": "data.lang ?? \"tr-TR\"",
    "$.headers.x-approver": "user.available && user.hasRole(\"approver\") ? \"yes\" : \"no\""
  }
}
```

The task is called with:

```json
{
  "Content-Type": "application/json",
  "x-channel": "web",
  "x-customer-id": "c-42",
  "x-request-id": "8b1f…",
  "x-amount": "1250.5",
  "x-correlation.id": "BASVURU-001",
  "Accept-Language": "tr-TR",
  "x-approver": "no"
}
```

| Case | Result |
|---|---|
| Configured header the block does not touch (`Content-Type`) | kept |
| Configured header the block writes (`x-channel`) | the block's value wins |
| Non-string value (`data.amount`) | sent as its JSON text (`1250.5` → `"1250.5"`; an object → its JSON) |
| Hyphen in the name | plain segment: `$.headers.x-channel` |
| Dot or space in the name | bracket segment: `$.headers['x-correlation.id']` |
| Expression yields `null` or reads a missing value | nothing written; the configured value stays |
| No `headers` (or `null`) in the configuration | an object is created first |
| Configured `headers` is not an object | the mapping fails |

- **`into: "$.headers"` replaces the whole object** — `{ "select": "$.data.outgoingHeaders", "into": "$.headers" }`
  drops `Content-Type`. `into` writes its target as it is, it never merges; add headers one `set` key at a time.
- A header cannot be removed: `null` means "write nothing". Send `"\"\""` for an empty value, or leave the header
  out of the task definition.

### URL from configuration

The `API_BASEURL` placeholder a C# `InputHandler` resolves with `GetConfigValue` is one expression:

```json
"input": {
  "set": {
    "$.url": "task.url.ToString().Replace(\"API_BASEURL\", config.get(\"Example:ApiBaseUrl\", \"http://localhost:3001\")) + \"/\" + urlEncode(data.customer.id) + \"?lang=tr\""
  }
}
```

- `task.url` is the configured URL (`task` is the task's `config`, a JSON view like `data`, so a string needs `.ToString()` before `Replace`).
- `config.get(key)` / `config.get(key, fallback)` read the host configuration — the same source and reach as `GetConfigValue`, connection strings included. Restricting what a domain's expressions may read is a separate, domain-owner decision.
- `urlEncode(value)` percent-encodes one URL component (`Uri.EscapeDataString`); splice every data value into a path or query through it. Dynamic Expresso has no `$"..."` interpolation: build strings with `+` or `string.Format`.
- Number formatting follows the server culture (`data.amount.AsDouble().ToString("0.00")` prints `1250,50` under `tr-TR`). Splice the JSON value itself instead: `urlEncode(data.amount)` and `"" + data.amount` use its JSON text (`1250.5`), which has no culture.

The output phase sees the same `task` after the input phase, so `"$.calledUrl": "task.url"` records the URL actually called.

**Cost.** The write-back is one copy of the `config` and one `Configure` parse per execution: 0.94 µs against a 0.02 µs task clone for an Http task, and a whole input block (URL + `config` + `urlEncode` + `select`/`into` + one header) 3.6 µs (BenchmarkDotNet short job, `DeclarativeInputBenchmarks`).

## Output phase

- `select` reads `result` (root `$` is `result`; default `$.body`); `into` and `set` target instance data (not the root `$`).
- Without `into`, the selected object merges at the data root, like an `OutputHandler` returning `Data`. Without `select`, `into` takes `result.body`.
- **`result` is read in place, not rebuilt.** `body` is the task's response `Data` as it is (a `JsonElement`, as the HTTP and Dapr invokers return it), else that `Data` serialized once with the runtime's JSON options; a `select` or `result.<member>` walks only that member; the whole `{ body, headers, statusCode }` object is written out only when `result` itself is used as a value (`select: "$"`, `set: { "$.x": "result" }`). Every value written to the delta is a subtree-sized copy, so the delta never keeps the task response alive.
- **Deliberate difference — deep response bodies.** Before the in-place read, the runner re-serialized the whole response through default JSON options (depth limit 64), so a body nested deeper than about 63 levels failed the mapping even when the `select` / `set` touched only a shallow part. Now a `select` or member read on such a body succeeds; the limits that remain are 64 levels for the value copied into the delta and for materializing the whole `result`, and 256 levels (the runtime's `JsonSerializerConstants.JsonOptions.MaxDepth`) for serializing a non-JSON `Data`.
- **Ordering:** `into` first, then `set` entries in declaration order (a later entry wins on a collision). One delta object results in **one** instance-data append, so x-encryption, versioning and ETag are inherited. Encrypted fields read in an expression are the stored tokens and stay tokens when written back. An empty delta writes no version.

### What each part reads and writes

| Part | Reads | Writes |
|---|---|---|
| `select` | the task response only: `result` = `{ body, headers, statusCode }`; `$` is `result`, default `$.body` | — |
| `into` | the value `select` found | the delta (not instance data) |
| `set` key | — | the same delta |
| `set` value | every variable: `result`, `data`, `input`, `instance`, `now`, `user`, `task`, `config`, `context`, `headers` | — |

The block never edits instance data directly: it builds a delta, and the delta is merged once at the end. `data` in
a `set` expression is therefore the data **before** this task's output — what `into` just wrote to the delta is not
visible through `data`; read it from `result`.

### Worked example

Instance data before the task, and the task response:

```json
{ "customer": { "id": "c-42", "name": "Ayşe" },
  "kyc": { "level": "basic", "score": 10, "history": [1, 2] } }
```

```json
{ "statusCode": 200,
  "headers": { "x-request-id": "r-991" },
  "body": { "score": 87, "band": "A", "checks": ["id", "address"] } }
```

Block:

```json
"output": {
  "select": "$.body.score",
  "into": "$.kyc.score",
  "set": { "$.kyc.checkedAt": "now", "$.kyc.state": "instance.currentState" }
}
```

1. The delta starts empty.
2. `select` + `into`: `result.body.score` is `87`, written at `$.kyc.score` (missing objects are created) →
   `{ "kyc": { "score": 87 } }`.
3. `set`, in declaration order →
   `{ "kyc": { "score": 87, "checkedAt": "2026-11-24T10:15:03.123Z", "state": "kyc-check" } }`.
4. The delta is merged into the latest data and stored as a new data version:

```json
{ "customer": { "id": "c-42", "name": "Ayşe" },
  "kyc": { "level": "basic", "score": 87, "history": [1, 2],
           "checkedAt": "2026-11-24T10:15:03.123Z", "state": "kyc-check" } }
```

`kyc.level` and `kyc.history` survive (not in the delta), `kyc.score` changes 10 → 87, `band` and `checks` are not
written (`select` took only `score`).

### `select` / `into` combinations

With the response above:

| Authored | Behaviour | Delta |
|---|---|---|
| `select` + `into` | the selected value at `into` | `"select": "$.body.band", "into": "$.kyc.band"` → `{ "kyc": { "band": "A" } }` |
| `into` only | `select` defaults to `$.body` | `"into": "$.kyc.response"` → `{ "kyc": { "response": { "score": 87, "band": "A", "checks": [...] } } }` |
| `select` only | the selected **object's members** merge at the data root | `"select": "$.body"` → `{ "score": 87, "band": "A", "checks": [...] }` |
| `select` only, selected value not an object | the mapping fails | `"select": "$.body.score"` → `87` cannot merge at the root |
| neither | only `set` runs | whatever `set` writes |

Response headers and status code are readable too:

```json
"set": {
  "$.audit.requestId": "result.headers[\"x-request-id\"]",
  "$.audit.httpStatus": "result.statusCode",
  "$.kyc.approved": "result.body.score.AsInt32() >= 70"
}
```

### Collisions and edge cases

- **Later wins:** `into: "$.kyc.score"` then `set: { "$.kyc.score": "result.body.score.AsInt32() * 2" }` → `174`.
- **A parent path replaces the whole subtree in the delta:** a `set` key `"$.kyc"` replaces what `into` wrote under
  `kyc`; the stored `kyc.level` still survives, because the final merge is deep.
- **No path inside a non-object:** after `into` wrote `87` at `$.kyc.score`, a `set` key `"$.kyc.score.detail"` fails
  the mapping (`'score' exists and is not a JSON object`).
- **`data` is the previous value:** `{ "$.kyc.previousScore": "data.kyc.score" }` writes `10`.
- **Failure is all or nothing:** any error other than a missing value (see [Missing values](#missing-values)) writes
  no part of the delta.

### Effect on instance data

The delta goes through the same merge as every other write ([merge semantics](../domain/instance-data-merge-concept.md#merge-semantics--the-sharp-edges-client-facing)):

1. **Objects merge deeply, key by key**; a field not in the delta is never removed.
2. **Arrays are replaced whole**: `{ "$.kyc.history": "result.body.checks" }` turns `[1, 2]` into
   `["id", "address"]`. This is why write paths take no `[n]` / `[*]`; `select` may read `"$.body.checks[0]"`.
3. **A field cannot be removed**: a `null` from `set` writes nothing, and the merge never clears an existing key
   with `null`. Use a sentinel (`""`, `false`, `"cleared"`) where "cleared" must be expressible.
4. **Nothing written, no version.**

In a function or extension the delta is not written to instance data: it is the task output an `OutputHandler`
would return, in the function or extension response.

## Missing values

A missing `select` path, a missing leaf, `?.`, or member access on a missing intermediate (`data.customer.id` with no `customer`) makes that entry **write nothing** — no error. A skipped `select` (path not found) or `set` entry (missing intermediate) raises the Debug log `TaskDeclarativeMappingEntrySkipped` (EventId 10272) with the task key, the phase and the path or `set` key — never the value; a plain null value or `?.` result skips silently. Every other evaluation error (bad cast, unwritable path, role provider failure) **fails the mapping**: the error boundary applies and `TaskDeclarativeMappingFailed` is logged. Inconsistency to know: legacy `context.*` paths still fail on a missing member.

## User

`user` is available in task mappings on **caller-driven (User) hops** of state `onEntries` / `onExits` and transition `onExecutionTasks`. On System hops (fired timers, SubFlow resume, continuations) `user.available` is `false` and reading `user.roles` / `user.hasRole` — in a `set` expression or through an input `select` such as `$.user.roles` — fails the mapping (fail-closed, never an empty set). That failure is **deterministic, not transient**: a `TaskExecution` failure whose message says to guard the read with `user.available`, never `Authorization:110006`. `110006` (transient, retryable) is kept for a real role-provider failure while resolving the caller's roles. An input `select: "$.user.available"` reads the flag on any hop. Functions and extensions always see `user.available == false`. Roles are resolved once, and only when an expression uses them. Script mappings never see the caller. Provider and surfaces: [Script Caller Roles](script-caller-roles.md).

**Persisting roles is the author's responsibility.** An output `set` such as `{ "$.approval.roles": "user.roles" }` writes the caller's roles into instance data, where every later reader of that data (state and data functions, scripts, other callers allowed to read the instance) sees them. Write roles into data only when that exposure is intended, and protect the field (`x-roles`, `x-masking`) as you would any other caller-derived value.

## Dynamic Expresso outside task mappings

A `ScriptCode` with `location: "dynamicExpresso"` (`encoding: "NAT"`) is an inline expression in these slots — no
`.csx`, no Roslyn compile. **One standard for every slot:** the condition rules and the string keys bind the same
variables as the value slots below — `data`, `input`, `instance` (camelCase), `now`, `user`, `headers` — next to the legacy rule
context `context` (PascalCase members), which is unchanged: every existing `context.*` expression compiles and returns
what it did (same interpreter options, same number type). No `task`, `config` or `result`. Variables are bound lazily:
a `context`-only rule builds the legacy context and nothing else, and resolves no roles.

| Slot | Result | Evaluator (signature) | Variables |
|---|---|---|---|
| Transition `rule` (auto), view `rule`, transition / function `schemas[]` and contract `rule`, long-poll `rule`, state-notification `rule` | `bool` | `RoutingConditionEvaluator` → `DynamicExpressoConditionEvaluator` (`condition`) | `data`, `input`, `instance`, `now`, `user`, `context`, `headers` |
| CacheAside `key`, function cache `keyExpression` / `generationKeyExpression` | `string` | `CacheKeyEvaluator` / `FunctionAppService` → `DynamicExpressoValueEvaluator` (`key`) | the same, plus the functions `sha256(text)` and `varyKey(context)` |
| Transition `resourceLock.keyExpression` | `string` (the lock key) | `ResourceLockStep` → `DynamicExpressoValueEvaluator` (`key`) | as above |
| Agent tool `binding.code` (tool kind 2) | `object` | `AgentToolCatalog` (`agent-tool`) | `args`, `data` (`ExpandoObject`), `now` (`DateTime`) — **the one exception**, see below |

Changes for an existing expression in a condition or key slot: assignment operators are disabled (`x = 1` no longer
compiles; nothing in a rule or a key should write), and the compile cache keys these slots under their new signature
names (`condition`, `key`), so the first evaluation after an upgrade compiles once.

#### `input` per trigger

`input` is **one value per hop**: the raw request payload that triggered the hop
(`TransitionExecutionContext.HopInput`), captured when the hop's context is created and never touched by task
outputs. Every step of the hop reads the same value — resource-lock key (order 25), task mappings and CacheAside keys
(30 / 40 / 60), agent prompt and decision (65), automatic `rule` (80), a timer armed by the hop (90) — whichever step
built the hop's shared script context first. It is never the merged instance data and never a task response
(`context.Body` keeps its meaning, unchanged).

| Trigger | `input` |
|---|---|
| Manual transition (sync, or the async-accept job replaying it) | the request body (`attributes`) |
| Instance start | the start request's `attributes` (the start hop and the workflow `timeout.mapping` armed by it) |
| `updateData` | its body |
| Event-triggered transition | the payload the event path hands the pipeline (the event mapping's result) |
| Retry | the retry request's body (`null` when it has none) |
| Fired timer (scheduled transition, workflow timeout) | `null` |
| Automatic continuation hop (the auto transitions chained after a hop) | `null` — a continuation carries no new request payload |
| SubFlow resume (completion, fault, cancellation), long-poll ack resume | `null` |
| Function call | the function's request body (contract `rule`, input `schemas[]`, `keyExpression` / `generationKeyExpression`, and its tasks' mappings); `null` without a body |
| Transition `schemas[]` rule | the transition request body (`null` on the schema read path, which has no request) |
| Read paths: view selection, state / data functions, long-poll and state-notification rules, function discovery | `null` |

A rule therefore tells the triggering hop from a continuation with `input == null`, and a resource-lock key can use
the request directly (`"seat:" + input.seat`).

Task mappings (declarative `input` / `output` blocks) keep reading the transition record body
(`context.CurrentTransition.Data`) when the task step attached the record, exactly as before; it is the hop's request
body except where a legacy transition `mapping` script rewrote the record body, or a retry reused the original record
(then the original body). Without a record (the hop's context was built earlier, e.g. by the resource lock) they read
the hop's `input`.

What `input` carries for special fields, as the pipeline exposes it:

- `x-storage` file field: the **handle** in every pipeline expression (task mappings, lock key, auto rules, timers).
  File admission swaps a request's `content` for the stored handle before any step runs, and `input` follows it —
  never the bytes. Copying the handle is harmless (it carries metadata, not bytes); an input block that sends it to a
  task sends the handle, and a task that needs the bytes uses a C# mapping with `GetFileAsync`.
  **Exception — the transition and start `schemas[]` rule** runs *before* the swap, by necessity: the schema it
  selects validates the request in its write shape (with `content`), and the swap runs only after validation so
  that a rejected request writes nothing to the store. There `input` holds the request as sent, in one of three
  shapes:

  | The client sent | `input.<field>` in the `schemas[]` rule |
  |---|---|
  | inline content (`transport: inline`) | `{ name, mimeType, size, content: "<base64>", …other members }` — `name`, `mimeType` and `size` are the client's claim (the runtime later ignores the client's `size`) |
  | a streamed upload (`transport: stream`) | `{ file: "<upload id>", …other members }` — no `name`, `mimeType` or `size` (they are on the upload ticket) |
  | an echo of the stored handle | `{ file: "<guid>", …what the client sent back }` — replaced by the stored handle later |

  So a rule such as `input.document.mimeType == "application/pdf"` matches only inline uploads. Do not select a
  schema by file metadata; read a member the client sends explicitly next to the file (`input.document.documentType`),
  which every shape keeps. See [File Storage](file-storage.md#where-the-swap-happens).
- `x-encryption` field (`encrypt` or `hash`): the **plaintext** the client sent, by decision. Encryption and hashing
  happen in the instance-data write funnel, after `input` was captured; `input` is neither masked nor tokenized. This
  is the same trust boundary C# scripts already have: `context.Body` of a transition's own task scripts and
  `context.CurrentTransition.Data` carry the same plaintext. `data` holds the stored form (the `encrypt` token, the
  `hash` digest).

The write funnel protects the schema's own paths, not values. So for an output `set` entry:

| Entry | Stored |
|---|---|
| `"$.email": "input.email"` — back to the field's own path | the token (the funnel seals it) |
| `"$.tckn": "input.tckn"` — a `hash` field to its own path | the digest |
| `"$.contactNote": "input.email"` — an unprotected path | **plaintext**: the copy removes the protection, and nothing at runtime refuses or logs it |
| `"$.contactNote": "data.email"` — the stored token to another path | refused (`EncryptedValueReservedException`): a token only opens at its own path |

An input block that sends `input.email` to a task (`"$.body.email": "input.email"`, e.g. to a KYC service) sends the
plaintext, which is usually the point; `data.email` sends the token. Dynamic Expresso cannot open a stored token
(there is no `DecryptAsync` in an expression): a task that needs the plaintext of a value stored earlier uses a C#
mapping with `context.Instance.DecryptAsync(path)`.

The runtime does not check these copies at publish: components are published independently, the master schema may
not be published yet, and a field can become `encrypt` after the workflow. **Forge** warns while the author edits,
when the workflow's master schema is in the project: an output `set` entry that reads an `encrypt` / `hash` field from
`input` (or an object that contains one, e.g. `input.customer`) into a path that is not itself protected. It sees
plain `input.a.b` / `input["a"]` reads, not values passed around inside an expression, so it is a hint rather than a
guarantee. See [Field masking and encryption](../domain/field-masking.md).

Headers and query parameters are not part of `input`.

Missing values: `data.x?.y`, a missing member or index reads as `null` (a comparison is then just false); a conversion
of a missing value (`data.nope.AsDouble()`) throws, and a throwing rule **does not select** (not satisfied / next entry
/ not admitted / not sent), exactly as a throwing `context.*` rule always did; a throwing key fails as before (CacheAside
and function cache run uncached or fail per their settings, a lock key fails the step with `Execution:200012`). JSON
values need a conversion to compare: `data.amount.AsDouble() > 1000`, `data.customer.id.ToString() == "c-42"` — a key
concatenates them as text (`"cust:" + data.customer.id`).

`user` follows [User](#user) and [Script Caller Roles](script-caller-roles.md), fail-closed per site: on a hop with no
caller (`user.available` false — System hops, and every surface whose accessor is closed: resource lock, function cache
keys, CacheAside keys) reading `user.roles` / `user.hasRole` throws, so the rule does not select and the key fails —
never an empty role set. When the role provider cannot answer (`Authorization:110006`), the evaluator returns that
error and the accessor stays failed, so each site keeps its documented outcome: selection surfaces `503`, the
automatic rule fails the step (no DefaultAutoTransition), a lock key fails with `110006` and is never retried as C#.
Reading `user` at all (even `user.available`) on a caller-driven hop resolves the roles once, as in task mappings.

**The agent-tool exception.** `tools[].binding.code` keeps its own variables (`args`, `data`, `now`; no `headers`) and assignment
setting. Its `data` is an `ExpandoObject` and existing expressions rely on that dynamic access with implicit numeric
conversion (`data.amount > 5`); the standard `data` view (`RuleJsonDynamic`) needs `data.amount.AsDouble() > 5` and
would break them. A tool kind 1 (`binding.input` / `binding.output` blocks) already gets the standard mapping variables.

The value slots below bind the mapping variables of a condition (decision D-1): `data`, `input`, `instance`
(camelCase), `now`, `user`, `context`, `headers` — no `task`, `config` or `result`, assignment disabled. They are evaluated by
`ValueSlotExpressionEvaluator` (signature `mapping-condition`; the decision has its own, `agent-decision`).

| Slot | JSON | Result | Extra variables |
|---|---|---|---|
| Transition timer | `transitions[].timer` | timer schedule (below) | — |
| Workflow timeout | `attributes.timeout.mapping` | timer schedule; on failure the static `timeout.timer.duration` is used, as before | — |
| SubFlow timeout override | `states[].subFlow.overrides.timeout.mapping` | same as the workflow timeout (the child resolves it) | — |
| Agent prompt | `states[].agent.prompt` (`location` + `code`) | `string` (null fails) | — |
| Agent decision, mode `script` | `states[].agent.decision.mapping` | transition key `string`; `null` = no decision (the fallback runs); a key that is not a candidate also takes the fallback | `outcome`, `candidates` |

Timer schedule (decision D-2): `TimeSpan` → duration from now; `DateTime` / `DateTimeOffset` → that instant (a
`DateTime` with no kind is UTC); a string (or a JSON string from `data`) → an ISO-8601 duration (`"PT15M"`), else an
ISO-8601 date-time (invariant culture, UTC when it has no offset); `null` → immediate (`TimerSchedule.Immediate`: now + 2 s). Any other value — a number
included, its unit is ambiguous — fails: write `TimeSpan.FromSeconds(...)`. Pass a `double` to the `TimeSpan`
factories (`TimeSpan.FromMinutes(15.0)`): an `int` literal is ambiguous between their `long` and `double` overloads.
A JSON value is not a number by itself: `TimeSpan.FromMinutes(data.waitMinutes.AsDouble())`.

`outcome` (the agent run): `completed` (bool), `output` (the structured output, members read as
`outcome.output.score`), `text` (the output as compact JSON text), `decision` (the output's `decision` member),
`reason`, `failureReason`. `candidates`: `count`, `keys` (string array), `has("approve")`, `find("approve")` (null when
absent), `candidates[0]`; each candidate has `key`, `when`, `whenSource`. Example:
`candidates.has(outcome.decision) && outcome.output.score.AsDouble() < 0.5 ? outcome.decision : "manual-review"`.
JSON values compare as views: call `.ToString()` / `.AsDouble()` / `.AsInt32()` / `.AsBoolean()` before comparing.

Agent hints (`decision.hintsMapping`) stay C# only: they return a dictionary.

Compatibility. Before these slots — and the resource-lock key — honoured the label, the script engine ignored the
location, so a C# script labelled `dynamicExpresso` ran as a script. Such a script keeps working: when it does not
evaluate as an expression it runs as the C# script it was, with one warning — `ResourceLockKeyExpressionRanAsScript`
(EventId 10273) for a lock key, `ScriptLabelledDynamicExpressoRanAsScript` (EventId 10274, `Slot` = `timer`,
`agent prompt`, `agent decision`) for a value slot. When neither works the expression's error is reported (code
`Expression.ValueSlotFailed`; a lock key `Execution:200012`, an empty lock key `Execution:200011`). Each slot keeps its
failure semantics: a failing transition timer fails the step, a failing timeout falls back to the static duration, a
failing agent prompt or decision fails the hop. A caller-roles provider failure (`Authorization:110006`) is never retried
as C#. Publish compiles these expressions and reports one that does not compile as a **warning** (it still publishes:
it may be one of those C# scripts), never an error.

Other value slots (transition / SubFlow / event / output mappings, state-notification metadata) still run as C#
scripts.

### Declarative blocks of a nested task

Two places run a task outside a binding and now accept the binding's declarative blocks; the runner is the same
(`DeclarativeTaskMappingRunner`), so the variables, phases and rules above apply unchanged:

| Where | JSON | Exclusive with |
|---|---|---|
| CacheAside source task | `config.sourceInput`, `config.sourceOutput` (a `MappingBlock` each) | `config.sourceMapping` |
| Agent tool kind 1 | `tools[].binding.input`, `tools[].binding.output` | `tools[].binding.mapping` |

Publish validates both like a binding's blocks (`TaskComponentValidator` for the CacheAside task, errors at
`sourceInput.…` / `sourceOutput.…`; `AgentComponentValidator` for the agent, errors at `Agent.tools[i].binding.input.…`)
and rejects a script and blocks together. A definition stored with both before that check existed runs the script (its
legacy meaning), the same rule as a task binding. The CacheAside `sourceOutput` result is what is cached.

## Blocks outside task bindings

Three more sites take a declarative block in place of a C# `mapping` script: a transition's `output`, a SubFlow state's `input` and its `output`. The block grammar (`select`, `into`, `set`) and the expression rules are those of a task binding; what differs is the variables, the target document and where the result goes.

- `input` builds what goes **to** a callee, `output` writes what comes **back**. A single-block site rejects the other block at publish: a transition carrying an `input` member is a publish error (`'input' is not supported on a transition; use 'output'.`).
- `select` reads `result` when the site has one, otherwise the variables (`$.data…`, `$.input…`).
- A block and a C# `mapping` on the same site is a publish error; a stored definition holding both runs the script.

| Site | Variables | `select` root | Target | Delta starts | Result goes to |
|---|---|---|---|---|---|
| Transition `output` | `data`, `input`, `instance`, `now`, `user`, `context`, `headers` | variables (no `result`) | instance data | empty; `"select": "$.input"` keeps the whole request | transition record body, the single data append, `CurrentTransition.Data` |
| SubFlow `input` | the same set | variables | the child's start document `{ attributes, headers, key, tags }` | empty | the child start request |
| SubFlow `output` | the same set plus `result`; `input` is null; `headers` / `user` are the child settler's (see [carried headers](../architecture/subflow-execution.md#carried-headers-from-the-child)) | `result`, default `$.data` | instance data | empty | the single data append of the parent |

`result` of a SubFlow output is `{ data, status }` with `status` either `"completed"` or `"faulted"`.

### Transition `output`

Applies to `transitions[]`, `startTransition`, shared transitions and the well-known `cancel` / `updateData` / `exit`. Automatic and scheduled transitions reject `output`, like `mapping`: an automatic hop has no request to map. An empty delta is no data version (as a script returning null).

```json
{
  "key": "submit",
  "target": "review",
  "output": {
    "select": "$.input.customer",
    "into": "$.customer",
    "set": { "$.submittedAt": "now", "$.submittedBy": "user.id" }
  }
}
```

### SubFlow `input`

For `type: S` only; a `P` SubProcess rejects both blocks (it is started by its own task). The target document is `{ attributes, headers, key, tags }`:

- `select` needs `into`; `key` must be a string and `tags` an array of strings, anything else fails the start before a child is created.
- `$.headers.sub` written blank stays blank (the [instance-call credential rule](../architecture/subflow-execution.md)).
- `input` is the request of the hop that entered the SubFlow state: null on an automatic chain and on a retry restart, where `user` is the retrying requester, as for the script.

```json
"subFlow": {
  "type": "S",
  "process": { "key": "kyc-check", "domain": "core", "version": "1.0.0", "flow": "sys-flows" },
  "input": {
    "set": {
      "$.attributes.customerId": "data.customer.id",
      "$.attributes.amount": "input.amount",
      "$.headers.x-channel": "\"web\"",
      "$.key": "data.customer.id",
      "$.tags": "new [] { \"kyc\" }"
    }
  }
}
```

### SubFlow `output`

Runs on completion **and** on fault; `result.status` tells them apart (a script cannot today). Child data arrives in plaintext, as for the script. Script `Mutations` have no block equivalent.

```json
"subFlow": {
  "type": "S",
  "process": { "key": "kyc-check", "domain": "core", "version": "1.0.0", "flow": "sys-flows" },
  "output": {
    "select": "$.data.score",
    "into": "$.kyc.score",
    "set": { "$.kyc.outcome": "result.status", "$.kyc.settledAt": "now" }
  }
}
```

### Failures

The error codes are the scripts' (`Execution` transition mapping error, `SubFlowInputMappingFailed`, `SubFlowOutputMappingFailed`) under the same `Script.Execute` spans; new log events are 10277 `TransitionDeclarativeOutputFailed`, 10278 `SubFlowDeclarativeInputFailed` and 10279 `SubFlowDeclarativeOutputFailed`. A caller-roles provider failure stays `Authorization:110006` (retryable; on the SubFlow output completion path the event is redelivered); reading `user.roles` with no caller is a permanent failure.

## Publish validation

`MappingBlockValidator` parses every path and compiles every `set` expression (warming the cache). Error locations are camelCase JSON paths, for example `states[review].onEntries[0].output.set['$.kyc.score']: ...`, unlike older validators' PascalCase member paths.
