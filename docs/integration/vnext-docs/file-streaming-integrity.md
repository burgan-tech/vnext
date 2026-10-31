---
product: vnext-docs
milestone: v0.0.102
status: pending
---

# x-storage streaming, integrity and file processing — Handoff for vnext-docs

## Context

- vNext PR: branch `claude/vnext-file-upload-streaming-f7c134` (vnext-client-sdk-core #137, #142, #145)
- Milestone: v0.0.102

## What changed in the runtime

- Definition-based streamed uploads (`x-storage.transport`, `POST …/workflows/{flow}/files`, single-use tickets).
- Every file read is verified against the handle (`size` + sha256 `eTag`); an object a Dapr 1.17+ S3 binding stored
  `aws-chunked` framed is served as the original; anything else is 503 `Instance:100047`.
- Scripts can process files with in-memory System.IO streams and write the result back (`OpenRead`,
  `PutFileAsync`); a list of banned members.
- DaprBinding tasks return the binding's answer to the output mapping and can name the object
  (`FileObjectKey`), which is how a flow deletes, reads or lists its files. The runtime has no delete endpoint.
- A `rawResponse` function returning `byte[]` or a `ScriptFile` answers the bytes.

Source of truth: `docs/runtime/file-storage.md`, `docs/custom-script-helpers.md`,
`docs/runtime/task-invocation-routing.md`, `vnext-meta/unreleased/file-streaming-integrity.json`.

## What this product must do

- Client consumption: a "large file upload" page (when to choose `stream`, the two-step upload → reference flow,
  ticket expiry, 413 above the limit).
- Developer guide: "processing a file in a script" with the read → stream → `PutFileAsync` example and the
  allowed/banned table.
- Developer guide: "deleting and listing files" with the DaprBinding task, the mapping setters and the
  `Modes:daprbinding=Local` requirement; the retention pattern (housekeeping flow or retention state).
- Operations: the S3 `aws-chunked` note (Dapr 1.17+ over https; store support, http, sidecar pin).

## Contract and examples

Use the request/response and script examples in `docs/runtime/file-storage.md` § Streamed uploads and
§ Processing files in scripts as they are.

## Acceptance

- The four pages exist and their examples match `docs/runtime/file-storage.md`.

## Out of scope

- Client SDK (FilePicker/DocViewer) documentation; it follows the vnext-client-sdk-core release.
