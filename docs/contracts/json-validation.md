# JSON Validation

## Purpose

JSON validation protects workflow definitions, transition payloads, and instance data
contracts before invalid state reaches the domain model or persistence layer.

## Boundaries

Validation lives in the domain validation package and is invoked by application/domain
paths that accept JSON payloads. Infrastructure may cache validators, but validation
semantics and error mapping should remain stable for consumers.

## Architecture Flow

1. Caller provides JSON schema and data.
2. Vocabulary-only or runtime-unsupported schema keywords are sanitized when needed.
3. Schema is parsed by `Json.Schema`.
4. Data is evaluated with hierarchical output and format validation.
5. Successful validation returns `Result.Ok()`.
6. Failed validation maps evaluation details into stable validation results.
7. Optional vocabulary details are serialized into problem detail `Detail`.

## Contracts

| Output | Contract |
| --- | --- |
| Error code | Uses `WorkflowErrorCodes.ValidationErrors`. |
| Message | Stable high-level message: JSON schema validation failed. |
| Field details | Path, keyword, code, message, label, schema path, and parameters. |
| Culture | Included in `SchemaValidationProblemDetails`. |
| Vocabulary metadata | Controlled by `SchemaValidationOptions.IncludeVocabularyDetails`. |

## Error Tree Flattening (non-negotiable)

A rejected payload MUST name the field that failed. Two rules make that hold:

1. **A node's own errors and its child details are independent, never alternatives.**
   In the hierarchical output a keyword's error sits on the node that owns the keyword, and that
   node also gains child `Details` as soon as the schema evaluates any subschema (`properties`,
   `additionalProperties`, a nested object). So a root-level `required` failure is an error *on the
   root* sitting beside a full set of valid children. `FlattenErrors` therefore emits a failing
   node's own errors **and** recurses into its children.
2. **An invalid evaluation never flattens to an empty list.** A failing subtree that yields nothing
   reportable still surfaces its node, rendered as a `Validation failed` placeholder.

Why it is load-bearing: `WorkflowResultActionResultMapper.TryGetSchemaValidationDetails` treats an
empty `Errors` list as "not a schema validation problem" and falls through to the generic RFC7807
ProblemDetails response. An empty list therefore costs the caller **both** the field names and the
`{"error":{…,"validationErrors":[…]}}` response shape at once — the client receives
`"errors":{}` and cannot tell which field to fix. Regression-pinned by
`JsonSchemaValidationMapperTests`.

Member names are **instance paths** (`root`, `customer`, `customer.ownerUserId`), never keyword
names. `required` is a keyword; a client cannot bind a message to it.

## Two Validation Layers at Transition Intake

A request body is checked twice before the instance is admitted (before the Busy flip, before an
async job is queued, before a start persists the instance row). Both failures answer `400` with
`validationErrors[].members`, and the instance is untouched.

Order at intake: transition schema → state-machine policy → master-schema preview. The preview runs
last on purpose: it reads the stored document, so a request the policy refuses (wrong state,
unauthorized actor) never reaches it and is never answered with a master-schema message. If the
preview itself fails unexpectedly it accepts and logs `IntakeMasterSchemaPreviewSkipped` (10276) —
the write funnel validates the same content.

| Layer | Schema | Validates | Where |
| --- | --- | --- | --- |
| 1 | Transition schema (`schema` / `schemas[]`) | The body as sent | `TransitionValidationService` |
| 2 | Master schema (the flow's `schema`) | A preview of the body merged onto the latest data | `IInstanceDataWriteService.PreviewAppendAsync` |

Layer 2 exists because the master schema is otherwise only checked when the merged data is written,
inside the pipeline, where a failure faults the instance: one field a closed master schema
(`additionalProperties: false`) does not allow used to end in a faulted instance instead of a `400`
(vnext#1255). The preview runs the write funnel's own merge and validation in memory and writes
nothing. **The funnel still validates every write and stays the authority** — the preview reads the
loaded snapshot, not the head under the row lock, and is only the early refusal.

The preview covers the caller's own body and nothing else. It accepts, and leaves the verdict to the
funnel, whenever it cannot reproduce what the funnel would validate:

| Not previewed | Why |
| --- | --- |
| Transition with a `mapping` | The mapping's output is what is written, and previewing it would run the script before admission. **Known gap, see below.** |
| Automatic / Scheduled / Event hops | Not a client request to answer with `400`. |
| Request relayed to an active SubFlow | The leaf records the body and previews it against its own master schema. |
| No master schema, or one that failed to load | Not validated by the funnel either (unchanged). |
| Master schema with `x-encryption` paths, or data carrying `ENCRYPTED:` / `HASHED:` values | The funnel validates the opened, re-hashed document, which needs the instance secret. |
| Body carrying an `x-storage` file node | The file swap runs after validation; the master schema describes the stored handle. |
| A merge that changes nothing | The funnel writes and validates nothing for a duplicate. |

### Known gap: transitions with a `mapping`

The same kind of violation has two outcomes, depending on something the caller cannot see:

| Transition | A body that leads to a master-schema violation |
| --- | --- |
| No `mapping` | Refused at intake: `400` `App:900002`, instance untouched, nothing queued. |
| With a `mapping` | Not previewed. The violation is found only when the mapped output is written, inside the pipeline and after admission: the request is not answered `400` and the instance is faulted. |

A caller's body is usually an input of the mapping, so a client request can cause the second outcome;
it is not only an authoring mistake. The mapping is not run at intake because a mapping script is not
side-effect free (it can write files, call bindings and read related instances) and intake runs before
the instance is reserved.

This is a recorded gap, **subject to change and not a contract** — do not build on the faulted outcome.
Decision: council session `2026-11-23-mapped-transition-master-schema-preview`
([decision log](../agent-council/sessions/README.md)). Getting a faulted instance out of that state is
vnext#1252; preventing the fault for mapped transitions is a separate task.

`WorkflowExecution:InstanceDataWrite:IntakeMasterSchemaPreview` (default `true`) switches layer 2
off; the funnel's validation is unaffected. The validation runs under the `Schema.ValidateMaster`
span.

## Failure Modes

- Schema parse failure indicates invalid schema configuration.
- Format validation failure is returned as field-level validation detail.
- Unknown vocabulary metadata should not leak unstable internal schema terms to clients.
- Cached validator behavior must not return stale rules after schema changes.

## Observability

Validation failures should expose consumer-safe error codes and messages. Logs can include
schema path and keyword, but should avoid dumping full payloads when they may contain PII.

## Change Safety

- Changing error code, detail shape, or HTTP status is a breaking API contract.
- New vocabulary metadata should map through `JsonSchemaValidationMapper`.
- Keep custom messages and labels culture-aware.
- Add tests when changing sanitization, mapping, or `SchemaValidationOptions`.

## References

- `src/BBT.Workflow.Domain/Validation/JsonSchemaValidator.cs`
- `src/BBT.Workflow.Domain/Validation/CachedJsonSchemaValidator.cs`
- `src/BBT.Workflow.Domain/Validation/JsonSchemaValidationMapper.cs`
- `src/BBT.Workflow.Domain/Validation/SchemaValidationOptions.cs`
- `src/BBT.Workflow.Domain/Validation/SchemaValidationProblemDetails.cs`
- `src/BBT.Workflow.Domain/Validation/JsonSchemaVocabularySanitizer.cs`

