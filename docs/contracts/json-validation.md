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

