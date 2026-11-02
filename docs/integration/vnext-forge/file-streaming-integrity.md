---
product: vnext-forge
milestone: v0.0.102
status: pending
---

# x-storage transport and script file APIs — Handoff for vnext-forge

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/1144 (vnext-client-sdk-core #137, #142, #145)
- Milestone: v0.0.102
- Schema: https://github.com/burgan-tech/vnext-schema/pull/163

## What changed in the runtime

- `x-storage` takes an optional `transport`: `inline` (default, base64 `content` in the JSON body), `stream`
  (only the upload endpoint; inline `content` from a client is refused, 400 `Instance:100048`) or `any`.
- Upload endpoint for `stream`/`any` fields: `POST /api/v1/{domain}/workflows/{flow}/files?path=<field path>&name=<file
  name>[&version=]`, raw body, `Content-Type` = the file's media type, `Content-Length` required. Answers 201 with a
  ticket; the start or transition body then carries `{ "file": "<ticket.file>" }` at the same path before
  `expiresAt`. A ticket is single-use and bound to its flow, path and component.
- The swap keeps the file node's other members (#137): a row's `documentType` survives.
- Scripts: `ScriptFile.OpenRead()`, `ScriptBase.PutFileAsync(domain, flow, instance, component, byte[] | Stream,
  name, mimeType)`, `ScriptBase.FileObjectKey(file)`; `DaprBindingTask.SetOperation / SetData / SetMetadata` are
  public. The sandbox allows the in-memory System.IO stream types and the compression streams, and bans a set of
  members (path constructors, non-generic `Activator`, `Type.GetType(string)`, reflection-by-name, `Environment.Exit`).

Source of truth: `docs/runtime/file-storage.md`, `docs/custom-script-helpers.md` § Stream types,
`vnext-meta/unreleased/file-streaming-integrity.json`.

## What this product must do

- Schema designer: offer `transport` on an `x-storage` node (enum `inline` | `stream` | `any`, default `inline`).
- Quick Run file widget: for a `stream` or `any` field, upload through `POST …/files?path=` and put
  `{ "file": "<id>" }` (plus the row's own members) into the body; keep the base64 path for `inline` and `any`.
  Show a refused inline upload (`Instance:100048`, "streamed uploads only") as a field error.
- Script editor completions and diagnostics: the new `ScriptBase` / `ScriptFile` / `DaprBindingTask` members, and
  the sandbox's allowed System.IO types and banned members (the compile error text is
  `banned member '<member>'`).

## Contract and examples

```json
"documents": { "type": "array", "items": { "type": "object",
  "x-storage": { "binding": "vnext-blob-s3", "transport": "stream" } } }
```

```http
POST /api/v1/core/workflows/kyc/files?path=documents[]&name=scan.tiff
Content-Type: image/tiff
Content-Length: 31457280

201 { "file": "6f1c…", "component": "vnext-blob-s3", "path": "documents[]", "name": "scan.tiff",
      "mimeType": "image/tiff", "size": 31457280, "eTag": "<sha256>", "expiresAt": "…" }
```

## Acceptance

- A `transport: stream` field uploads a file above 10 MiB from Quick Run and the instance's handle has the ticket's
  `size` and `eTag`.
- A mapping using `OpenRead` + `MemoryStream` compiles without a sandbox diagnostic in the editor.

## Out of scope

- Deleting files from the designer (deletion is a flow's own DaprBinding task).
