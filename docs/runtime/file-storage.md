# File Storage (`x-storage`)

A master-schema field marked `x-storage` keeps its file **bytes in a Dapr output binding** (local disk,
S3/MinIO, ...) instead of the instance record. The client sends the bytes once, base64 inside the normal
request body; the runtime stores them, and from that point every record (instance data, transition record,
job, outbox row) and every read carries a small **handle**. Fields without `x-storage` behave as before
(bytes stay inline). Introduced in 0.0.100 (vnext-client-sdk-core#101, runtime half).

Code: `src/BBT.Workflow.*/Files/` (`FileOffloadService`, `FileAdmission`, `InstanceFileAppService`,
`DaprBindingFileBlobStore`), `FileStorageSchemaParser` (publish rules), `FileFunctionHandler`
(`functions/file`), `InstanceController` (`internal/file`). Spans: `Files.Offload` in
[trace-span-tree](trace-span-tree.md). Logs: `WorkflowLogs` event ids 2049x.

## Declaration

On a master-schema **property**, or on the `items` schema of an array property:

```json
"identityDocument": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } },
"files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "vnext-blob-s3" } } },
"documents": { "type": "array", "items": { "type": "object", "properties": {
  "doc": { "type": "object", "x-storage": { "binding": "vnext-blob-s3" } } } } }
```

- `binding` names a Dapr output binding component on the **orchestration** sidecar
  (`bindings.localstorage` locally, `bindings.aws.s3` or MinIO elsewhere). Required, non-empty string; no
  other member is allowed.
- Reachable only through nested `properties`, plus **one** array level (`files[]`, and a property inside
  that array's item schema such as `documents[].doc`). Not under `$defs`, combinators (`allOf`/`anyOf`/`oneOf`),
  conditionals, nested arrays, the schema root, or inside another file node. A misplaced declaration is a
  **publish error**, not a silent no-op.
- The node must be `type: object` and describes the **persisted** handle (below). Declaring `content` under
  its `properties` or `required` is a publish error: `content` never reaches master-schema validation.
- Placement is enforced at publish by `FileStorageSchemaParser.Validate` (and by the vnext-schema
  `x-storage` rule, which can lag the runtime).

## Shapes

**Write** (request body, only on the way in):

```json
{ "name": "passport.pdf", "mimeType": "application/pdf", "size": 204800, "content": "<base64>" }
```

**Persisted handle** (instance data, transition record, job/outbox, every read):

```json
{
  "component": "vnext-blob-s3",
  "file": "7c9e4c2a-5f7e-4a51-9c11-0b8b6c1d2e3f",
  "name": "passport.pdf",
  "mimeType": "application/pdf",
  "size": 204800,
  "eTag": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
  "owner": { "domain": "onboarding", "flow": "kyc-main-flow", "instance": "8394783-..." }
}
```

- `file`: generated GUID (default `D` format), never the client file name. `size`: the decoded byte count;
  the client's `size` is ignored. `eTag`: SHA-256 of the decoded bytes, lower-case hex, no prefix (quoted in
  the HTTP `ETag`).
- `owner`: the instance whose write created the object (runtime `APP_DOMAIN`, flow key, instance id) -
  informational, tells a client which instance's `functions/file` to call.
- Object key in the component: `{FileStorage:KeyPrefix}{file}`.

**Echo** (a client that read the instance and sends the field back): `{ "file": "<guid>", ... }` with no
`content`. The runtime replaces the node with the **stored** handle (any client-sent metadata is discarded).

## Write rules

Applied per `x-storage` node of an incoming payload:

| Node | External payload (client body) | Trusted payload (runtime-produced) |
|---|---|---|
| `content` present, `file` absent | Decode base64 (invalid or non-string: `400`), `create` in the binding, node becomes the handle | same |
| `content` and `file` both present | `400` `FileReferenceInvalid` | `400` `FileReferenceInvalid` |
| `content` absent, `file` present | The same `file` id must exist at the same path in the instance's `LatestData` (arrays: any element of the same array). Found: node becomes the stored handle. Not found: `400` `FileReferenceInvalid`. A `start` has no `LatestData`, so always `400` | Kept as is |
| `null` / absent | Field cleared; nothing is deleted from the store | same |

- **External** = a client body: public `start`, public transition, a relayed SubFlow forward, event-driven
  start. **Trusted** = data the runtime produced: SubFlow input mapping (local and `sub/instances/start`),
  Start/SubProcess trigger tasks, DirectTrigger transitions, transition mapping output, task outputs, SubFlow
  output mapping. A trusted payload is never reference-checked (a mapping author is trusted to put a valid
  handle there).
- **Two passes.** Pass 1 validates every node and plans; pass 2 writes the blobs and swaps the nodes. A
  rejected request therefore writes nothing to the store.
- A store failure is `503` `FileStoreUnavailable`, synchronous, **before** any `202`. A failing `start`
  creates no instance (the swap runs before the instance row, so a retry with the same key is not turned
  into an idempotent no-op). Inside a pipeline hop (funnel, mapping step) the failure takes the normal error
  path.

| Code | Name | HTTP | Meaning |
|---|---|---|---|
| `Instance:100047` | `FileStoreUnavailable` | 503 | The binding call failed (also: `get` of a missing object, see limitations); the request was not applied and can be retried |
| `Instance:100048` | `FileReferenceInvalid` | 400 | Both `content` and `file`, bad base64, non-string `content`, unknown/foreign reference, missing `file` query on `functions/file` |
| `Instance:100049` | `FileNotFound` | 404 | `functions/file`/`internal/file`: the file is not in this instance's `LatestData`, or its path is hidden by `x-roles` |

## Where the swap happens

After schema validation, before anything is persisted or enqueued:

| Entry | Swap point |
|---|---|
| `start` | `InstanceCommandAppService.OffloadStartFilesAsync`, after start validation and before `Instance.Persist` |
| sync transition | `FileAdmission` at the validation point, before admission |
| async transition | `FileAdmission` before the accept lock, the Busy flip, the job row and the outbox row |
| transition mapping output | `CreateTransitionRecordStep` (Trusted mode) |
| instance-data write funnel | `InstanceDataWriteService.AppendAsync` / `AppendExplicitAsync` (Trusted mode, defence in depth) |

- A request a parent **forwards to its active SubFlow** is not swapped by the parent; the leaf records it and
  swaps it against **its own** master schema (see [SubFlow Transition Proxy](../architecture/subflow-transition-proxy.md)).
  A relay from an older parent that still carries `ChainReserved=true` is swapped at the leaf as well.
- The `Files.Offload` span appears **only when there is work**: an object payload on a flow whose master
  schema declares `x-storage`; in the Trusted hooks only when the element actually carries a `content`
  member at an `x-storage` path. Otherwise no span.
- **Raw body.** Scripts read `ScriptContext.RawBody`; after a swap on an External payload the runtime
  rewrites it so scripts never see the bytes. When the captured body is the standard envelope, only its
  `attributes` member is replaced (the other members are kept); any other shape has the whole body replaced.
  Trusted hops never touch the outer request's raw body.

## Reads

### `functions/file` (client)

`GET|HEAD /{domain}/workflows/{workflow}/instances/{instance}/functions/file?file=<guid>`

- The handle is looked up **only** in this instance's `LatestData` at its `x-storage` paths: no SubFlow
  descent, no history. The binding name and object key come from the record, never from the caller.
- Authorization is in the engine, in this order: the state's `queryRoles` (denied: `403`, via
  `ITransitionAuthorizationManager.IsQueryAllowedAsync`), then the `x-roles` grants of the file's path and
  of every ancestor path (hidden: `404`, so existence is not revealed). This is a deliberate exception to
  the 2026-09-23 read-path decision that the gateway owns `queryRoles`: a file endpoint can return raw
  identity documents.
- Not in `LatestData` (or a different flow sharing the schema): `404` `FileNotFound`. Missing `file`
  parameter: `400`.
- `If-None-Match` equal to `"<eTag>"` (weak comparison, `*`, lists) answers `304` without a store read; the
  authorization checks run first.
- `200`: raw bytes; `Content-Type` = stored `mimeType` (else `application/octet-stream`);
  `ETag: "<eTag>"`; `Accept-Ranges: bytes`; `Cache-Control: private, max-age=31536000, immutable`;
  `X-Content-Type-Options: nosniff`; `Content-Disposition: inline; filename*=UTF-8''...`. `Range` answers
  `206`/`416` (and honours `If-Range`). `HEAD` returns the headers only.
- `Content-Encoding: identity` is set on purpose: compression and `Range` do not mix (the response
  compression middleware would gzip an already-sliced `206` body), and a pre-set encoding makes it skip the
  response.

### `GetFileAsync` and `internal/file` (service-to-service)

`ScriptBase.GetFileAsync(domain, flow, instance, file)` returns `ScriptFile { Content, Name, MimeType, Size,
ETag }`. Same domain: in process. Another domain: `GET .../instances/{instance}/internal/file?file=` on the
owning domain (discovery + Dapr/HTTP transport), answering the bytes with `ETag` and `X-File-Handle` (base64
UTF-8 JSON of the handle).

**No authorization** on this path (no caller identity, `queryRoles` or `x-roles`): trusted-network posture
like the other `internal/*` routes, see [API and service contracts](../contracts/api-and-service-contracts.md).
The handle is still looked up in that instance's `LatestData`, so the binding never comes from the caller.
Whoever owns ingress/NetworkPolicy must keep the route unreachable from outside the mesh.

## Configuration

| Where | Setting |
|---|---|
| Runtime | `FileStorage:KeyPrefix` (string, default empty): prepended to every object key, e.g. `vnext-runtime/` in a shared bucket. **Never change it after the first write**: stored handles carry no key, so existing objects become unreachable |
| Helm (`vnext-helm-charts`) | `global.blobStorageComponents: []`, rendered like `pubsubComponents`; scoped to the **orchestrator** app id only |
| Local | `etc/orchestration/dapr/components/vnext-blob-local.yaml` (`bindings.localstorage`, `rootPath: /blobs`); orchestration sidecar started with `--max-body-size 64Mi` (all compose files) |

## Dapr limits

- The binding `get` is **buffered**: the whole object is read into memory, so a `Range` request is an
  in-memory slice (a network saving, not a memory saving).
- **Request size.** A client request first hits Kestrel `Kestrel:Limits:MaxRequestBodySize` (10485760 = 10 MiB
  in the orchestration and execution `appsettings.json`; the raw-body buffer is bound to it). Oversize
  requests get `413` before any swap. Base64 inflates the file by about 4/3, so one request carries roughly
  7.5 MiB of file unless a host raises the limit (raise it together with the sidecar's).
- The sidecar's `--max-body-size 64Mi` applies to the binding call and the Dapr hops, not to the client
  request; the remote gateway enforces the same 64 MiB cap on a cross-domain `internal/file` read.
- Large-body behaviour of the Dapr binding path (dapr/dapr#9177) is **unverified** here: the measurement
  that would settle it is pending (integration window). Do not quote a throughput or size claim for files
  near the cap until it has run.

## Known limitations

| Limitation | Effect / guidance |
|---|---|
| Orphans on rollback | The blob is written before the unit of work commits; a rollback after `create` leaves an unreferenced object |
| Orphans on `409` after store | The swap runs before reserve/accept; a `409` (Busy, completion window) after the store leaves the objects behind |
| No delete, replace or retention | Clearing or replacing a field never deletes the old object; there is no lifecycle or garbage collection (use the store's own lifecycle rules) |
| Task records hold what a mapping puts there | `InstanceTask.Request/Response` are not protected: a mapping that puts bytes into a task request stores them there |
| `TrustedPayload` does not cross domains | Start/SubProcess trigger tasks, DirectTrigger and proxied forwards that hit another domain use its public endpoints, so a content-less reference in such a body is `400` there (it fails closed). Send `content`, not references, cross-domain |
| Seed data is not offloaded | `DefinitionAppService` seed-data appends pass no workflow, so `x-storage` content in a deploy-time seed is stored inline |
| 10 MiB request cap | Client requests above Kestrel's 10 MiB `MaxRequestBodySize` (about 7.5 MiB of file after base64) are rejected with `413`; larger files need a raised host limit |
| 64 MiB remote cap after buffering | A cross-domain read buffers the object first and rejects above 64 MiB |
| Overlapping async proxy revert | Two overlapping async requests on one parent: one failed-forward revert can clear the other's Busy stamp (self-heals on the next child notification) |
| Missing object looks like an outage | A `get` of an object that is gone from the store reports `503`, like an outage |
| Schema load failure fails open | If the master schema cannot be loaded, the field list is empty and nothing is swapped (mitigated: the swap runs after schema validation) |
| Duplicate property names | A payload with duplicate member names in a file node is handled inconsistently between the content probe and the swap |

Machine-readable entries: `vnext-meta/known-issues.json` (`file-store-orphans-on-rollback`,
`file-store-no-delete`, `file-in-task-record`, `file-trusted-payload-cross-domain`,
`file-seed-data-not-offloaded`).
