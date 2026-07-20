# Form URL-Encoded JSON Parity Design

## Context

Issue #825 adds `application/x-www-form-urlencoded` request-body support to the public Orchestration endpoints for workflow start, transition execution, and function invocation. These endpoints already bind `application/json` bodies as `JsonElement?`; form input must therefore be normalized into the same JSON tree before the existing payload-mode and application pipelines run.

The implementation must preserve existing JSON behavior, remain scoped to the Orchestration host, expose the additional media type through OpenAPI, and reject ambiguous form shapes instead of silently losing or changing data.

## Form-to-JSON Contract

Form keys use bracket paths:

- `attributes[customer][name]=Ali` produces nested JSON objects.
- `tags[]=a&tags[]=b` and repeated `tags=a&tags=b` produce scalar arrays.
- `items[0][name]=A&items[1][name]=B` produces an indexed array of objects.
- Empty brackets are supported only for a trailing scalar-array segment. Ambiguous object-array input such as `items[][name]=A` is rejected with HTTP 400; callers must use numeric indices.
- Malformed brackets, negative or sparse indices, mixed object/array use at the same path, and scalar/container collisions are rejected with HTTP 400 and a clear model-binding error.

Scalar values use JSON-literal semantics in payload data:

- `30`, `1.25`, `true`, `false`, and `null` become their corresponding JSON value types.
- A JSON-quoted value such as `"00123"` becomes the string `00123`.
- Text that is not a valid JSON scalar literal, such as `Ali` or `Initial`, remains a JSON string.
- JSON objects and arrays are not accepted as scalar field values; structural data must use bracket paths. This avoids two competing representations for the same path.

For a standard payload, the envelope fields `key`, `stage`, and every `tags` element remain strings regardless of whether their text resembles a JSON literal. Values below `attributes` use JSON-literal semantics. For a raw payload, all leaves use JSON-literal semantics. Payload mode is resolved consistently with the existing controller behavior: `x-vnext-payload-mode` overrides auto-detection, otherwise a top-level `attributes` field selects standard mode and its absence selects raw mode.

## Architecture

`FormUrlEncodedJsonElementInputFormatter` remains in `BBT.Workflow.HttpApi.Shared` because it is an HTTP formatting component, but it is registered only from `AddOrchestrationApiModule`. Shared `AddAspNetCoreModules` continues to configure JSON and controllers without enabling form binding in Execution, Monitor, Inbox, or Outbox hosts.

The formatter is divided into three responsibilities:

1. Parse each form key into typed property, numeric-index, or trailing-append path segments.
2. Insert each form value into a mutable object/array tree while detecting malformed, ambiguous, sparse, or colliding paths.
3. Write the validated tree as JSON, applying envelope-aware scalar conversion based on the resolved payload mode.

Parsing failures are added to MVC model state and returned as formatter failures so `[ApiController]` produces a normal HTTP 400 response. No partially normalized payload is passed downstream.

## OpenAPI and Documentation

Orchestration MVC options advertise `application/x-www-form-urlencoded` for `JsonElement` and `JsonElement?` bodies. ApiExplorer/OpenAPI tests verify that Start, Transition, domain Function, and instance Function operations retain `application/json` and add the form media type.

A public contract document under `docs/contracts` records supported key syntax, scalar typing, payload-mode interaction, examples, and rejected ambiguous forms. XML comments point to the same rules but are not treated as the user-facing documentation.

## Testing

Tests follow red-green TDD and cover:

- JSON scalar typing for numbers, booleans, null, quoted strings, and ordinary text.
- Preservation of standard envelope strings, including a numeric-looking large `key` and numeric-looking tags.
- Nested objects, repeated scalar arrays, trailing `[]` arrays, and indexed arrays of objects.
- Clear failures for malformed brackets, unindexed object arrays, sparse/negative indices, and scalar/container conflicts.
- `standard`, `raw`, and auto-detected payload modes.
- MVC registration being present in Orchestration and absent from shared/Execution registration.
- Actual MVC body binding for form input and regression binding for `application/json`.
- ApiExplorer/OpenAPI request media types for all Start, Transition, and Function endpoint groups.

Targeted formatter, MVC, and OpenAPI tests must pass. The relevant test project is also run in full; unrelated baseline failures are reported separately rather than attributed to this feature.

## Non-Goals

- Multipart form data and file uploads are not supported.
- PHP-style unindexed arrays of objects are not inferred.
- Schema-driven coercion is not performed inside the formatter.
- Execution-service endpoints do not gain form-urlencoded support.
