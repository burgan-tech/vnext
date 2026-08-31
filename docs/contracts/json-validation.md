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

