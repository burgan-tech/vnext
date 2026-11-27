---
product: vnext-ai-toolkit
milestone: v0.0.107
status: pending
---

# HTTP task `multipart/form-data` body — Handoff for vnext-ai-toolkit

## Context

- vNext PR: the `feature/http-task-multipart` PR of milestone v0.0.107
- Milestone: v0.0.107
- Need: a domain (factoring) sends a scanned cheque PDF to an upload API that takes only
  `multipart/form-data` (`curl -F "file=@scan.pdf"`). Before this, no task type could: an HTTP task's
  `body`/`rawBody` is a string sent as UTF-8, a DaprBinding task's data too, and the script sandbox bans
  `System.Net.Http`.

## What changed in the runtime

A mapping script's `InputHandler` can add multipart parts to an `HttpTask` (type 6, and the deprecated 22):

- `AddMultipartFile(string name, byte[] content, string? fileName = null, string? contentType = null)` —
  the bytes go out unchanged; `fileName` defaults to `name`, `contentType` to `application/octet-stream`.
- `AddMultipartField(string name, string? value)` — a UTF-8 text part (null → empty).
- `ClearMultipart()` — drops every part.

With at least one part the request is multipart with a runtime-generated boundary; `body`, `rawBody`,
`contentType` and a `Content-Type` header are ignored. Parts are run-time only: the task JSON cannot
declare them and the component schema is unchanged. Source of truth:
`docs/runtime/task-executors-and-invokers.md` § HTTP task body, vnext-meta feature `httpTaskMultipartBody`.

## What this product must do

- `task-types.md` (HTTP, type 6): a line that an upload API is reached with multipart parts from the
  input handler, never with a base64 string in `body`.
- `csx-contracts.md`: the three `HttpTask` methods next to `SetBody` / `SetRawBody`.
- `component-task` skill: when the user describes an upload / `-F file=@…` endpoint, scaffold the task
  with `method: POST` and an `InputHandler` that calls `AddMultipartFile` (snippet below).
- Mention, with `file-fields.md`: an `x-storage` file is sent with `GetFileAsync` → `AddMultipartFile`.

## Contract and examples

```csharp
public class UploadMapping : ScriptBase, IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var http = (HttpTask)task;
        var base64 = (string)GetPropertyValue(context.Body, "content");
        http.AddMultipartFile("file", Convert.FromBase64String(base64), "scan.pdf", "application/pdf");
        // Never return the bytes in the ScriptResponse: it is written to the task journal.
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse { Data = context.Body }); // map the service's answer here
}
```

## Acceptance

- A task scaffolded for an upload endpoint uses `AddMultipartFile`, not `SetBody`/`SetRawBody` with base64.
- The references name the runtime version (`since` 0.0.107) so a domain on an older runtime is warned.

## Out of scope

- Declaring parts in the task JSON (`config.multipart`): not supported by the runtime; no schema change.
