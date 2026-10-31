---
product: vnext-helm-charts
milestone: v0.0.102
status: pending
---

# x-storage streaming uploads and binding tasks — Handoff for vnext-helm-charts

## Context

- vNext PR: branch `claude/vnext-file-upload-streaming-f7c134` (vnext-client-sdk-core #137, #142, #145)
- Milestone: v0.0.102
- Issues: https://github.com/burgan-tech/vnext-client-sdk-core/issues/145,
  https://github.com/burgan-tech/vnext-client-sdk-core/issues/142,
  https://github.com/burgan-tech/vnext-client-sdk-core/issues/137

## What changed in the runtime

- **Streamed uploads.** `POST /api/v1/{domain}/workflows/{flow}/files?path=&name=` takes a raw request body
  (no JSON, no base64) for an `x-storage` field whose `transport` is `stream` or `any`. The route raises the
  request size limit to `FileStorage:Upload:MaxBytes` for itself only. The file reaches the store as ONE Dapr
  binding message, so the orchestration sidecar's `--max-body-size` is the real ceiling.
- **New settings** (orchestration host): `FileStorage:Upload:MaxBytes` (default 67108864 = 64 MiB, the chart's
  `global.dapr.maxBodySize` default) and `FileStorage:Upload:TicketTtlMinutes` (default 60). A non-positive value
  fails the start.
- **The runtime's Dapr client no longer caps a received message at 4 MB.** No setting; files above 4 MB now read
  back through the binding. The sidecar's `maxBodySize` bounds both directions.
- **DaprBinding tasks** (type 2) can delete, read and list x-storage objects. The blob components are scoped to the
  orchestrator only, while binding tasks run in Execution by default. Either
  `Workflow__TaskInvocation__Modes__daprbinding=Local` on the orchestrator (the task then runs from the
  orchestration sidecar), or the component is scoped to the execution app-id as well.
- **No store SDK.** Every store stays a Dapr binding. A daprd 1.17+ sidecar uploads to S3 over https as
  `aws-chunked` with a CRC32 trailer (components-contrib builds the AWS transfer manager without options); a store
  that does not decode it keeps the frame. The runtime recovers such objects on read. vnext-meta known issue
  `dapr-s3-binding-frames-tls-uploads`.

Source of truth: `docs/runtime/file-storage.md` (§ Streamed uploads, § S3 writes through Dapr, § Deleting and
listing objects, § Dapr limits) and `vnext-meta/unreleased/file-streaming-integrity.json`.

## What this product must do

- Document the two `FileStorage__Upload__*` settings next to the existing `FileStorage__*` comments under
  `global.blobStorageComponents`, with the rule: `MaxBytes` ≤ the orchestrator's `maxBodySize`; raise both together.
- Add a commented `Workflow__TaskInvocation__Modes__daprbinding: "Local"` example to `orchestrator.appEnvConfig`
  for domains whose flows delete or list objects, or an optional value that adds the execution app-id to the
  blob components' `scopes`.
- Ingress / gateway in front of the orchestrator: the upload route needs a body limit of at least `MaxBytes`
  (nginx `client_max_body_size`, APISIX `client-control`), and request buffering off if the gateway supports it.
- Add a note for S3-compatible stores: with a daprd 1.17+ sidecar over https the store must decode `aws-chunked`
  (AWS S3, MinIO, Ceph do). Otherwise objects are stored framed; the options are an http endpoint in a trusted
  network or a pinned sidecar (`dapr.io/sidecar-image`, daprd 1.16.x) until Dapr exposes the checksum option.

## Contract and examples

```yaml
orchestrator:
  appEnvConfig:
    FileStorage__AllowedBindings__0: "vnext-blob-s3"
    FileStorage__Upload__MaxBytes: "67108864"        # ≤ global.dapr.maxBodySize (64Mi)
    FileStorage__Upload__TicketTtlMinutes: "60"
    Workflow__TaskInvocation__Modes__daprbinding: "Local"   # only when flows run DaprBinding tasks on blob components
```

## Acceptance

- A 30 MiB upload to `POST …/files` answers 201 through the ingress, and `functions/file` serves it back with the
  same sha256 (`eTag`).
- A flow's DaprBinding `list` task on the blob component succeeds (no `couldn't find output binding`).

## Out of scope

- Any store SDK or credentials outside the Dapr component.
- Changing the Dapr sidecar version policy; the pin is an operator choice, not a chart default.
