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

- `binding` names the store component: a Dapr output binding on the **orchestration** sidecar
  (`bindings.localstorage` locally, `bindings.aws.s3` or MinIO elsewhere). The runtime reaches every store through
  its Dapr binding and carries no store SDK. Required, non-empty string.
- `transport` (optional) chooses how a client may send the bytes: `inline` (default: base64 `content` in the JSON
  body), `stream` (a [streamed upload](#streamed-uploads-transport) referenced by `file`; inline `content` from a
  client is refused) or `any` (both). No other member is allowed.
- Reachable only through nested `properties`, plus **one** array level (`files[]`, and a property inside
  that array's item schema such as `documents[].doc`). Not under `$defs`, combinators (`allOf`/`anyOf`/`oneOf`),
  conditionals, nested arrays, the schema root, or inside another file node. A misplaced declaration is a
  **publish error**, not a silent no-op.
- The node must be `type: object` and describes the **persisted** handle (below). Declaring `content` under
  its `properties` or `required` is a publish error: `content` never reaches master-schema validation.
- Placement is enforced at publish by `FileStorageSchemaParser.Validate` (and by the vnext-schema
  `x-storage` rule, which can lag the runtime).
- A master schema without `$schema` is evaluated as Draft 2020-12, which ignores vocabulary keywords such as
  `x-storage` (`JsonSchemaBuild`). Before this, JsonSchema.Net's v1 default refused them: the schema published, and
  the first write failed with a 500 after its files were already stored. Declaring `$schema` is still recommended.

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
- `name`: the client's name with control characters replaced by `_`, capped at 255 characters (a short
  extension survives the cut). `mimeType`: the client's media type **normalised** by the header parser
  (`type/subtype` plus parameters), or `null` when it does not parse.
- **Valid handle.** The runtime acts on a handle only when `file` is a GUID in `D` format, `component` is an
  *allowed* binding, and `eTag` and every `owner` member are non-empty. Allowed = the `x-storage` bindings the
  flow's own master schema declares **plus** `FileStorage:AllowedBindings` (see Configuration). The component
  does not have to equal the field's own binding (a SubFlow can hand a parent's file down), but it can never
  be an arbitrary component or key. An invalid handle is never read (`404`) and never accepted on a write
  (`400`).
- `owner`: the instance whose write created the object (the domain being served, flow key, instance id; with [multi-domain hosting](multi-domain-hosting.md) that is the request's domain, not only `APP_DOMAIN`) -
  informational, tells a client which instance's `functions/file` to call.
- Object key in the component: `{FileStorage:KeyPrefix}{file}`.

**Echo** (a client that read the instance and sends the field back): `{ "file": "<guid>", ... }` with no
`content`. The runtime writes the **stored** handle's members over the node (a client-sent `size`, `mimeType` or
`eTag` is discarded).

**Other members of the node are kept** (vnext-client-sdk-core#137). The swap drops `content` and writes the seven
handle members; anything else the client put on the same object (a FilePicker row's `documentType`, per-file
metadata) stays and reaches the mapping and the record. A strict master schema (`additionalProperties: false` on the
file node) must declare such members under the node's `properties`.

## Write rules

Applied per `x-storage` node of an incoming payload:

| Node | External payload (client body) | Trusted payload (runtime-produced) |
|---|---|---|
| `content` present, `file` absent | Decode base64 (invalid or non-string: `400`), `create` in the binding, `content` is replaced by the handle members (other members kept) | same |
| `content` and `file` both present | `400` `FileReferenceInvalid` | `400` `FileReferenceInvalid` |
| `content` absent, `file` present | The same `file` id must exist at the same path in the instance's `LatestData` (arrays: any element of the same array) **and the stored handle must be valid**. Found: node becomes the stored handle. Not found or invalid: `400` `FileReferenceInvalid`. A `start` has no `LatestData`, so always `400` | Kept as is when it is a **valid handle**; otherwise `400` `FileReferenceInvalid` |
| `null` / absent | Field cleared; nothing is deleted from the store | same |

- **External** = a client body: public `start`, public transition, a relayed SubFlow forward, event-driven
  start. **Trusted** = data the runtime produced: SubFlow input mapping (local and `sub/instances/start`),
  Start/SubProcess trigger tasks, DirectTrigger transitions, transition mapping output, task outputs, SubFlow
  output mapping. A trusted payload is not reference-checked against `LatestData` (a mapping author may hand a
  file over from another instance), but every `file` node in it must be a valid handle (GUID file id, allowed
  component, complete metadata). Note: `sub/instances/start` sets the trusted flag and is not under
  `/internal`; the handle validation is what bounds it.
- **Two passes.** Pass 1 validates every node and plans; pass 2 writes the blobs and swaps the nodes. A
  rejected request therefore writes nothing to the store.
- A store failure is `503` `FileStoreUnavailable`, synchronous, **before** any `202`. A failing `start`
  creates no instance (the swap runs before the instance row, so a retry with the same key is not turned
  into an idempotent no-op). Inside a pipeline hop (funnel, mapping step) the failure takes the normal error
  path.
- **Master schema unavailable.** When the flow names a master schema that cannot be loaded, the runtime does
  not know the `x-storage` paths. If any object in the payload carries a `content` or `file` member, the write
  is refused with `503` `FileSchemaUnavailable` (start and transition: synchronously, before any instance row,
  job, outbox row or `202`; mapping step and funnel: thrown, normal error path). A payload with neither member
  is unaffected (no schema could make it file work) and proceeds as before. Retry once the schema resolves.

| Code | Name | HTTP | Meaning |
|---|---|---|---|
| `Instance:100047` | `FileStoreUnavailable` | 503 | The binding call failed (also: `get` of a missing object, see limitations); the request was not applied and can be retried |
| `Instance:100048` | `FileReferenceInvalid` | 400 | Both `content` and `file`, bad base64, non-string `content`, unknown/foreign reference, missing `file` query on `functions/file` |
| `Instance:100049` | `FileNotFound` | 404 | `functions/file`/`internal/file`: the file is not in this instance's `LatestData`, the stored handle there is invalid, or its path is hidden by `x-roles` |
| `Instance:100050` | `FileSchemaUnavailable` | 503 | The flow's master schema could not be loaded and the payload carries a `content`/`file` member (write), or a file was read (`functions/file`, `internal/file`); nothing was applied, retry |

## Streamed uploads (`transport`)

A field declared with `"transport": "stream"` or `"any"` takes its bytes from a separate upload request instead of a
base64 string in the JSON body, so a file is never inflated by base64, never held in a JSON body, and is bounded by
`FileStorage:Upload:MaxBytes` (default 64 MiB, the orchestration sidecar's `--max-body-size`; raise both together)
rather than the 10 MiB JSON body limit.

```http
POST /api/v1/{domain}/workflows/{flow}/files?path=files[]&name=scan.tiff[&version=1.0.0]
Content-Type: image/tiff
Content-Length: 31457280

<raw bytes>
```

```json
201 Created
{ "file": "6f1c…", "component": "vnext-blob-s3", "path": "files[]", "name": "scan.tiff", "mimeType": "image/tiff",
  "size": 31457280, "eTag": "<sha256 of the bytes received>", "expiresAt": "2026-10-31T13:00:00+00:00" }
```

Then reference the upload where the field lives, in a start or transition body, before `expiresAt`:

```json
{ "attributes": { "files": [ { "file": "6f1c…", "documentType": "invoice" } ] } }
```

- `path` is the field path as the schema folds it: `passport`, `customer.tax`, `files[]`, `documents[].doc`. Unknown
  path, a field whose `transport` is `inline`, or a missing `Content-Length` is `400` `Instance:100048`. A body above
  `MaxBytes` is `413` before any byte is stored. A store failure is `503` `Instance:100047`.
- The body is read from the request stream into one buffer of exactly `Content-Length` bytes (never a JSON string,
  never base64), hashed, and handed to the field's Dapr binding as one message: a Dapr output binding takes the whole
  object, so the hop to the sidecar is buffered. `size` and `eTag` are computed from the bytes received, never taken
  from the client; a body shorter or longer than its `Content-Length` is not stored.
- The upload is **staged** in the distributed cache (`vnext:{domain}:file-upload:{file}`) for
  `FileStorage:Upload:TicketTtlMinutes` (default 60). Nothing about it is in any instance yet.
- On the start or transition, a content-less `{ "file": … }` that is not an echo of the instance's own `LatestData` is
  looked up among the staged uploads. It is accepted only for the **same flow, path and component** it was uploaded for,
  once: the swap writes the handle with this instance as `owner` and removes the staging entry. The same upload twice in
  one body, an expired or unknown id, or an upload made for another path is `400` `Instance:100048`. Other members of the
  node (`documentType`) are kept, as for inline uploads.
- Knowing the GUID is the claim: it is random and only the uploader received it. The upload route carries no runtime
  authorization, like `start`; gate it at the gateway the same way.
- The route is excluded from raw-body capture (`RawRequestBodyBufferingMiddleware`, which would otherwise read the
  request into memory first) and raises the request size limit to `MaxBytes` for itself only. Aether's body logging
  does not read the request while request-body logging is off; it buffers only the small ticket response.

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
  `cancel` and `exit` are never forwarded: the preflight step short-circuits them to the parent's own record,
  so the parent swaps them (`SubflowForwardRule.WillForward`).
  A relay from an older parent that still carries `ChainReserved=true` is swapped at the leaf as well.
- **No file member, no schema load.** The offload acts only on `content` and `file` members, so start,
  admission and the mapping step first check whether any object in the payload carries one (raw-byte scan,
  exact walk only when the text has a backslash or one of the literals). If none does, the master schema is not
  loaded at all: about 46 ns per admission on a flow without `x-storage`, against 22 us (5 KB master schema) /
  85 us (20 KB) for the L1 schema load a file-carrying payload still pays (`FileAdmissionBenchmarks`).
- The `Files.Offload` span appears **only when there is work**: an object payload on a flow whose master
  schema declares `x-storage`; in the Trusted hooks only when the element actually carries a `content` or
  `file` member at an `x-storage` path. Otherwise no span.
- **Raw body.** Scripts read `ScriptContext.RawBody`; after a swap on an External payload the runtime
  rewrites it so scripts never see the bytes. When the captured body is the standard envelope, only its
  `attributes` member is replaced (the other members are kept); any other shape has the whole body replaced.
  Trusted hops never touch the outer request's raw body.

## Reads

### `functions/file` (client)

`GET|HEAD /{domain}/workflows/{workflow}/instances/{instance}/functions/file?file=<guid>`

- The handle is looked up **only** in this instance's `LatestData` at its `x-storage` paths: no SubFlow
  descent, no history. The binding name and object key come from the record, never from the caller, and the
  record must be a valid handle (else `404`; the store is never called with it).
- A `{domain}` that is not this runtime's answers like the `data` function (`NotFoundDomain`); same on
  `internal/file`.
- Authorization is in the engine, in this order: the state's `queryRoles` (denied: `403`, via
  `ITransitionAuthorizationManager.IsQueryAllowedAsync`), decided **before** the file is located so a denied
  caller learns nothing about which files exist; then the `x-roles` grants of the file's path and of every
  ancestor path (hidden: `404`, so existence is not revealed). This is a deliberate exception to
  the 2026-09-23 read-path decision that the gateway owns `queryRoles`: a file endpoint can return raw
  identity documents.
- Not in `LatestData` (or a different flow sharing the schema): `404` `FileNotFound`. Missing `file`
  parameter: `400`.
- `If-None-Match` equal to `"<eTag>"` (weak comparison, `*`, lists) answers `304` without a store read; the
  authorization checks run first.
- `200`: raw bytes; `Content-Type` = stored `mimeType` when it parses (else `application/octet-stream`, never
  a `500`); `ETag: "<eTag>"`; `Accept-Ranges: bytes`; `Cache-Control: private, max-age=31536000, immutable`;
  `X-Content-Type-Options: nosniff`; `Content-Security-Policy: sandbox; default-src 'none'` (always: the type
  is client-declared, so the response never runs as a document of this origin);
  `Content-Disposition: inline; filename*=UTF-8''...`, **`attachment`** for active types (`text/html`,
  `application/xhtml+xml`, `image/svg+xml`, `text/xml`, `application/xml`, `text/javascript`,
  `application/javascript` and any `+xml`). Control characters in the name are replaced; quotes and non-ASCII
  are escaped by the header writer. `Range` answers `206`/`416` (and honours `If-Range`). `HEAD` returns the
  headers only.
- `Content-Encoding: identity` is set on purpose: compression and `Range` do not mix (the response
  compression middleware would gzip an already-sliced `206` body), and a pre-set encoding makes it skip the
  response.
- **Streamed.** The body is written from a stream (`IInstanceFileAppService.OpenAsync` → `FileStreamResult`) over the
  binding's `get`, which is the whole object, [verified](#integrity-of-what-the-store-returns) before a byte is served.
  `Range` is sliced from that buffer (a network saving, not a memory saving).
- **Response buffering by Aether's body-logging middleware.** `UseHttpBodyLogging` copies every response that is not in
  `Telemetry:Logging:ExcludedPaths` into a `MemoryStream` before sending it, even with body logging off, so a
  downloaded file is held in memory once more on its way out. Adding `/functions/file$` and
  `/internal/file$` to that list removes the copy, but Aether's tracing filter uses the **same** list
  (`Logging.ExcludedPaths ∪ Tracing.ExcludedPaths`): the routes then lose their HTTP server span. The shipped
  configuration keeps the spans; an operator who prefers the memory saving adds the two patterns. The clean fix is in
  Aether (skip the response copy when response-body logging is off).

### Integrity of what the store returns

Every read (`functions/file`, `internal/file`, `GetFileAsync`) checks the stored
bytes against the handle before serving them (`StoredFileVerifier`, vnext-client-sdk-core#145):

| Stored bytes | Result |
|---|---|
| Length = `size` and sha256 = `eTag` | Served |
| A complete `aws-chunked` frame whose payload matches the handle | The payload is served; warning `20512` `FileRecoveredFromChunkedFrame` |
| Anything else | `503` `Instance:100047` `FileStoreUnavailable`; error `20513` `FileIntegrityMismatch` names the stored and expected sizes |

Why the frame case exists: Dapr 1.17+ `bindings.aws.s3` sends every upload over TLS as `aws-chunked` with a CRC32
trailer (`STREAMING-UNSIGNED-PAYLOAD-TRAILER`), and an S3-compatible server that does not decode that encoding stores
the frame itself (a 6265-byte PDF becomes a 6309-byte object). Over plain HTTP the binding signs the full payload and
stores the bytes as sent. See [S3 writes through Dapr](#s3-writes-through-dapr-aws-chunked) for why the runtime
cannot stop the framing on write and what an operator can do.

### `GetFileAsync` and `internal/file` (service-to-service)

`ScriptBase.GetFileAsync(domain, flow, instance, file)` returns `ScriptFile { Content, Name, MimeType, Size,
ETag }`. Same domain: in process. Another domain: `GET .../instances/{instance}/internal/file?file=` on the
owning domain (discovery + Dapr/HTTP transport), answering the bytes with `ETag` and `X-File-Handle` (base64
UTF-8 JSON of the handle).

**No authorization** on this path (no caller identity, `queryRoles` or `x-roles`): trusted-network posture
like the other `internal/*` routes, see [API and service contracts](../contracts/api-and-service-contracts.md).
The handle is still looked up in that instance's `LatestData` and must be valid, so the binding never comes
from the caller. Whoever owns ingress/NetworkPolicy must keep the route unreachable from outside the mesh.

`GetFileAsync` is a scripting API, and scripts run only in the Orchestration host; the Execution host has no
file reader, so the call is unavailable there (in practice never reached).

## Processing files in scripts

A script reads a stored file, processes it with System.IO streams and writes the result back as an x-storage file
(vnext-client-sdk-core#142-A):

```csharp
var source = await GetFileAsync(context.Runtime.Domain, context.Instance.Flow, context.Instance.Id.ToString(), handle.file);
using var input = source.OpenRead();                    // read-only, seekable MemoryStream over the verified bytes
var output = new MemoryStream();
// … hand input/output to a plugin library (PDF, TIFF, image) mounted in Scripting:Sandbox:PluginDirectory …
output.Position = 0;
var converted = await PutFileAsync(context.Runtime.Domain, context.Instance.Flow, context.Instance.Id.ToString(),
    "vnext-blob-s3", output, "converted.pdf", "application/pdf");
return new ScriptResponse { Data = new { convertedDocument = converted } };   // an x-storage path of the master schema
```

- `ScriptFile.OpenRead()` wraps the bytes in a new read-only stream on every call. The sandbox allows the System.IO
  stream types, not the file system ([custom-script-helpers § Stream types](../custom-script-helpers.md)).
- `PutFileAsync(domain, flow, instance, component, byte[] | Stream, name?, mimeType?)` stores the bytes and returns a
  `ScriptStoredFile` in the persisted handle shape (`component`, `file`, `name`, `mimeType`, `size`, `eTag`, `owner`).
  Size and eTag are computed from the bytes written. Placed at an x-storage path of the mapping or task output, it is a
  valid handle in the Trusted swap and is kept as is. Rules: the domain must be served here; the component must be an
  `x-storage` binding of that flow (latest version) or in `FileStorage:AllowedBindings`; the size is capped by
  `FileStorage:Upload:MaxBytes`. A refusal or a store failure throws (the task fails like any helper failure). A stream
  that cannot report its length is buffered first.
- `FileObjectKey(file)` answers the object key of a stored file (`FileStorage:KeyPrefix` + file id; a non-GUID is
  refused) for a `DaprBinding` task that reads, lists or deletes it (see [Deleting and listing](#deleting-and-listing-objects)).
- An object a script writes but no output keeps is an orphan, like any unreferenced object.

## Deleting and listing objects

The runtime has no delete endpoint and never deletes an object (`file-store-no-delete`). A flow that owns retention
deletes, reads or lists objects itself with a `DaprBinding` task (type `2`) on the store's Dapr binding component
(vnext-client-sdk-core#142-B/D):

- **Reach the component.** x-storage components are scoped to the orchestration app-id, while binding tasks run in
  the Execution service by default. Either set `Workflow:TaskInvocation:Modes:daprbinding = Local` (the task then runs
  from the orchestration sidecar, `LocalDaprBindingTaskInvoker`), or scope the component to the Execution app-id too.
- **Name the object.** In the input mapping, set the per-call shape on the task:
  `task.SetMetadata(new() { ["key"] = FileObjectKey(handle.file) })` (`bindings.aws.s3`; `fileName` for
  `bindings.localstorage`), and `task.SetOperation("delete" | "get" | "list")` / `task.SetData(…)` when the definition
  does not fix them. `FileObjectKey` is `FileStorage:KeyPrefix` + file id.
- **Read the answer.** The task's response reaches the output mapping: `Data` is the binding's body as JSON when it
  parses (e.g. `list`), as text, or as base64 (`get` of a binary object); `Metadata["DataEncoding"]` says which;
  `Headers` holds the binding's response metadata. A failure carries the sidecar's reason (for example
  `couldn't find output binding …`) instead of the generic Dapr client message.
- **Clear the field.** After a delete, set the field to `null` (an `updateData` or a mapping): a handle whose object is
  gone answers `503` (the binding reports a missing object like an outage).

Retention tied to the instance ("delete seven days after completion") cannot run on the completed instance itself (a
completed instance takes no transition): run it from a scheduled housekeeping flow that queries completed instances
(`GetInstances`) and deletes their objects, or keep the instance in a retention state with a timeout before its finish.

## Configuration

| Where | Setting |
|---|---|
| Runtime | `FileStorage:KeyPrefix` (string, default empty): prepended to every object key, e.g. `vnext-runtime/` in a shared bucket. **Never change it after the first write**: stored handles carry no key, so existing objects become unreachable |
| Runtime | `FileStorage:Upload:MaxBytes` (default 67108864, the sidecar's `--max-body-size`) and `FileStorage:Upload:TicketTtlMinutes` (default 60): [streamed uploads](#streamed-uploads-transport) |
| Runtime | `FileStorage:AllowedBindings` (string list, default empty): binding components a handle may name besides the flow's own declared `x-storage` bindings, for files handed across flows (SubFlow input, a mapping copying a parent's handle). **Helm deployments list their `blobStorageComponents` names here** (`FileStorage__AllowedBindings__0=...`) |
| Helm (`vnext-helm-charts`) | `global.blobStorageComponents: []`, rendered like `pubsubComponents`; scoped to the **orchestrator** app id only |
| Local | `etc/orchestration/dapr/components/vnext-blob-local.yaml` (`bindings.localstorage`, `rootPath: /blobs`); orchestration sidecar started with `--max-body-size 64Mi` (all compose files) |

## S3 writes through Dapr (aws-chunked)

The runtime never talks to a store directly: every write is a `create` on the component's Dapr binding. Since Dapr
1.17, `bindings.aws.s3` uploads through the AWS SDK's transfer manager, which is built without options
(`transfermanager.New(s3Client)` in components-contrib `bindings/aws/s3/s3.go`, unchanged on `main`) and therefore
always adds a CRC32 checksum. Over `https://` the SDK sends that checksum as a trailer, so the body goes out as
`Content-Encoding: aws-chunked` with `x-amz-content-sha256: STREAMING-UNSIGNED-PAYLOAD-TRAILER`. AWS S3, MinIO, Ceph,
LocalStack, s3mock, moto and SeaweedFS decode it; a store that does not keeps the frame as the object
(vnext-client-sdk-core#145). The transfer manager's checksum setting takes precedence over the SDK configuration, so
`AWS_REQUEST_CHECKSUM_CALCULATION=when_required` on the sidecar does **not** remove it (measured: daprd 1.18.2 stores
6309 bytes for a 6265-byte file with and without it). No component metadata controls it.

What the runtime does: every read is [verified](#integrity-of-what-the-store-returns), and a framed object whose payload
matches its handle is served unframed, so files written through Dapr 1.17+ read back exactly. What it cannot do is
store the object unframed. A consumer that reads the bucket directly (not through vNext) sees the frame. Operator
options, in order of preference:

| Option | Effect |
|---|---|
| Use a store that decodes `aws-chunked` (enable trailer/streaming-signature support on it) | Objects are stored as the file; nothing else changes |
| Reach the store over `http://` inside a trusted network (`disableSSL: "true"`) | The SDK signs the full payload, no frame; traffic is unencrypted |
| Pin the orchestration sidecar to daprd 1.16.x (`dapr.io/sidecar-image`) | 1.16 uploads unframed; a stopgap that holds back every other Dapr fix |
| Upstream: let components-contrib pass a checksum option to the transfer manager | The real fix; needs a Dapr release |

## Dapr limits

- The binding `get` is **buffered**: the whole object is read into memory, so a `Range` request is an
  in-memory slice (a network saving, not a memory saving). The same holds for `create`: one message per object.
- **Request size.** A client request first hits Kestrel `Kestrel:Limits:MaxRequestBodySize` (10485760 = 10 MiB
  in the orchestration and execution `appsettings.json`; the raw-body buffer is bound to it). Oversize
  requests get `413` before any swap. Base64 inflates the file by about 4/3, so one request carries roughly
  7.5 MiB of file unless a host raises the limit (raise it together with the sidecar's).
- The sidecar's `--max-body-size 64Mi` applies to the binding call and the Dapr hops, not to the client
  request; the remote gateway enforces the same 64 MiB cap on a cross-domain `internal/file` read.
- The runtime's Dapr client has no receive cap of its own (gRPC's 4 MB default is lifted in `AddDaprClients`): before,
  a binding `get` of an object above 4 MB failed with `ResourceExhausted` and the read answered `503`. The sidecar's
  `--max-body-size` is the bound in both directions.
- Measured locally (daprd 1.16.19, MinIO): a 30 MiB file uploads and reads back through `bindings.aws.s3`
  (FileStreamingLab). Files near the 64 MiB cap and throughput (dapr/dapr#9177) are not measured.

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
| Master schema unavailable is a `503` | No longer fails open: a payload carrying a `content`/`file` member is refused with `503` `FileSchemaUnavailable` while the master schema cannot be loaded (see Write rules); payloads without one proceed |
| Unclaimed streamed uploads | An upload never referenced before its ticket expires leaves its object in the store; the staging entry expires, the object does not (use the store's lifecycle rules, as for other orphans) |
| Concurrent claims of one upload | Two requests referencing the same staged upload at the same moment can both pass the lookup before either removes it; the file then has two referencing instances (owner: whichever swapped). Reference an upload from one request |
| Duplicate property names | A payload with duplicate member names in a file node is handled inconsistently between the content probe and the swap |
| Master schema reused as a transition schema | A transition schema validates the **write** shape. If a flow reuses its master schema as a transition schema, the `x-storage` nodes describe the persisted handle and cannot declare `content` (publish error), so a strict (`additionalProperties: false`) node rejects the upload. Give transitions their own schema for file fields |

Machine-readable entries: `vnext-meta/known-issues.json` (`file-store-orphans-on-rollback`,
`file-store-no-delete`, `file-in-task-record`, `file-trusted-payload-cross-domain`,
`file-seed-data-not-offloaded`, `file-async-proxy-overlapping-revert`, `file-missing-object-503`,
`file-duplicate-property-names`).
