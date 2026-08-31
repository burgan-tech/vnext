# Orchestration → Execution gRPC Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the orchestration→execution task-invocation hop to Dapr gRPC proxy mode behind a config switch, and close the DaprServiceTask question with an evidence-backed decision record.

**Architecture:** Execution keeps its HTTP controller AND gains a gRPC `TaskInvoker` service on the same Kestrel port (`Http1AndHttp2`); the wire payload stays the existing JSON DTOs carried as bytes. Orchestration's `RemoteInvokerService` selects HTTP or gRPC by `ExecutionApi:Transport`; which delivery path a deployment uses is decided solely by the Execution pod's `dapr.io/app-protocol` annotation, so rollback is config-only.

**Tech Stack:** .NET 10, Dapr .NET SDK 1.17.9 (`CreateInvocationInvoker` proxy mode), `Grpc.AspNetCore` + `Grpc.Tools` (Google.Protobuf codegen), xUnit + NSubstitute + Shouldly.

**Spec:** `docs/superpowers/specs/2026-08-26-execution-grpc-transport-spec.md`

## Global Constraints

- **Do NOT push to origin.** Local commits on `feature/trace-span-tree` only (user instruction).
- **Do NOT use `InvokeMethodGrpcAsync` or the raw `InvokeService` API** — deprecated in Dapr v1.15 (spec, Evidence). Proxy mode only.
- The HTTP invoke endpoint is **kept**, not replaced. Behavior of the HTTP path must be byte-identical after refactoring.
- Payload serialization must use the same `System.Text.Json` defaults the HTTP endpoint uses today (ASP.NET Core web defaults: camelCase). Never introduce a second serialization convention.
- Code default for the new switch is `"http"`; `"grpc"` is opted into per environment via appsettings/Helm.
- Message size limits: 64 MB send+receive on both client and server (matches sidecar `http-max-request-size: "64"`).
- No transport retries on either path (side-effecting tasks; documented in `RemoteInvokerService` remarks).
- All new logging goes through `WorkflowLogs.cs` LoggerMessage extensions per repo standards — EXCEPT inside `RemoteInvokerService`, which today uses raw `_logger.LogDebug/LogError`; match the file's existing style there (repo rule: match surrounding idiom).
- Test regression gate: failing-test NAME SET vs master baseline (`/private/tmp/claude-502/-Users-U0B006-Documents-repos-burgan-tech-vnext/771178c9-bba8-4b0b-8de9-3e512a61e4ae/scratchpad/master-failures.txt` holds the known-red subset; master carries 191 known failures overall) — no NEW names.

---

### Task 1: Decision record — DaprServiceTask stays on the HTTP API

**Files:**
- Create: `docs/runtime/dapr-invocation-transport.md`
- Modify: `docs/README.md` (add the page under the Runtime section, matching how sibling `docs/runtime/*.md` pages are listed)

**Interfaces:**
- Consumes: spec section "Decision 1" and "Evidence gathered before deciding".
- Produces: the canonical answer to "why doesn't DaprServiceTask use gRPC?" — later tasks and future evaluations reference this page.

- [ ] **Step 1: Write the decision record**

Create `docs/runtime/dapr-invocation-transport.md` with exactly this structure (fill prose from the spec — every Evidence bullet must appear, including the two verbatim deprecation strings and the `dapr-http-status` behavior):

```markdown
# Dapr Invocation Transport: What Uses gRPC, What Stays HTTP, and Why

## TL;DR
| Path | Transport | Why |
|---|---|---|
| Orchestration → Execution task invoke | gRPC proxy mode (opt-in per env) | Typed internal contract, single inbound surface, supported path |
| DaprServiceTask → external domain apps | HTTP API (unchanged) | gRPC→HTTP invocation is deprecated for removal; targets are HTTP apps |
| App → sidecar state/lock/pubsub calls | SDK-chosen (unchanged) | Deferred — evaluate after the above lands |

## The three hops (why "protocol: http" in Helm was never the knob)
[app→own-sidecar chosen by SDK method / sidecar↔sidecar always gRPC / sidecar→app = app-protocol]

## DaprServiceTask: the evidence
[Dapr v1.15 pkg/api/grpc/grpc.go InvokeService deprecation notices — quote both strings.
Non-2xx → gRPC error, status only in dapr-http-status header → AcceptedStatusCodes
contract cannot survive. Verdict + re-open condition (gRPC-capable targets, proxy mode).]

## Orchestration → Execution: the design
[Proxy mode, both server surfaces alive, app-protocol as the single switch,
rollback story, mixed-version constraint. Link the spec.]
```

- [ ] **Step 2: Link it from `docs/README.md`**

Open `docs/README.md`, find the Runtime grouping (where `runtime/trace-span-tree.md` is listed), add one line for the new page in the same list style.

- [ ] **Step 3: Commit**

```bash
git add docs/runtime/dapr-invocation-transport.md docs/README.md
git commit -m "docs(dapr): decision record — DaprServiceTask stays on the HTTP invocation API"
```

---

### Task 2: Proto contract + payload serializer in Execution.Abstractions

**Files:**
- Create: `src/BBT.Workflow.Execution.Abstractions/Protos/task_invoker.proto`
- Create: `src/BBT.Workflow.Execution.Abstractions/Grpc/TaskInvokePayload.cs`
- Modify: `src/BBT.Workflow.Execution.Abstractions/BBT.Workflow.Execution.Abstractions.csproj`
- Test: `test/BBT.Workflow.Application.Tests/Execution/TaskInvokePayloadTests.cs`

**Interfaces:**
- Consumes: existing `TaskInvokeRequest`, `TaskInvokeResponse`, `TaskEnvelope`, `TaskTraceContext` from `BBT.Workflow.Execution.Abstractions` (namespace `BBT.Workflow.Execution`).
- Produces:
  - Generated types in namespace `BBT.Workflow.Execution.Grpc`: `TaskInvoker.TaskInvokerClient`, `TaskInvoker.TaskInvokerBase`, `InvokeRequest { string TaskType; string TaskKey; ByteString PayloadJson; }`, `InvokeReply { ByteString PayloadJson; }`.
  - `static class TaskInvokePayload` with:
    - `static ByteString Serialize<T>(T value)`
    - `static T Deserialize<T>(ByteString payload)`

- [ ] **Step 1: Add packages and proto compilation to the csproj**

In `src/BBT.Workflow.Execution.Abstractions/BBT.Workflow.Execution.Abstractions.csproj`, inside the existing `<ItemGroup>` with PackageReferences add (versions: add `<GrpcPackageVersion>` / `<GoogleProtobufPackageVersion>` properties to `Directory.Build.props` next to the existing `OpenTelemetryPackageVersion` property, using the latest stable of `Grpc.Net.Client`/`Grpc.Tools`/`Google.Protobuf` that restores on net10.0 — check with `dotnet add package --dry-run` or nuget.org; do NOT guess in the plan-edit, pin what restore accepts):

```xml
<PackageReference Include="Google.Protobuf" Version="$(GoogleProtobufPackageVersion)" />
<PackageReference Include="Grpc.Net.Client" Version="$(GrpcPackageVersion)" />
<PackageReference Include="Grpc.Tools" Version="$(GrpcToolsPackageVersion)">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

And a new item group:

```xml
<ItemGroup>
  <!-- Both stubs from one project: orchestration consumes the Client, the Execution
       host derives the service from the Base. -->
  <Protobuf Include="Protos\task_invoker.proto" GrpcServices="Both" />
</ItemGroup>
```

- [ ] **Step 2: Write the proto**

`src/BBT.Workflow.Execution.Abstractions/Protos/task_invoker.proto` — exactly the contract from the spec:

```proto
syntax = "proto3";

package bbt.workflow.execution.v1;

option csharp_namespace = "BBT.Workflow.Execution.Grpc";

// Task invocation surface of the Execution service, served over Dapr gRPC proxy mode.
// The payload is the SAME JSON the HTTP endpoint exchanges, carried as bytes — the
// contract is JSON-shaped (JsonElement bindings, object result data) and this migration
// deliberately changes the transport, not the serialization semantics.
service TaskInvoker {
  rpc Invoke (InvokeRequest) returns (InvokeReply);
}

message InvokeRequest {
  string task_type = 1;
  string task_key = 2;
  bytes payload_json = 3; // TaskInvokeRequest as UTF-8 JSON (web defaults / camelCase)
}

message InvokeReply {
  bytes payload_json = 1; // TaskInvokeResponse as UTF-8 JSON (web defaults / camelCase)
}
```

- [ ] **Step 3: Write the failing test for the payload serializer**

`test/BBT.Workflow.Application.Tests/Execution/TaskInvokePayloadTests.cs`:

```csharp
using System;
using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Grpc;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution;

/// <summary>
/// Pins the gRPC payload serializer to the HTTP endpoint's JSON conventions. If these
/// two ever diverge, the same Execution build would parse a request differently
/// depending on which transport delivered it.
/// </summary>
public sealed class TaskInvokePayloadTests
{
    [Fact]
    public void RoundTrip_PreservesTheTaskInvokeRequest()
    {
        var binding = JsonSerializer.SerializeToElement(new { url = "https://x", method = "GET" });
        var request = new TaskInvokeRequest
        {
            Envelope = new TaskEnvelope
            {
                TaskType = "3",
                TaskKey = "get-iban-history",
                Binding = binding,
                InstanceId = Guid.NewGuid()
            },
            TraceContext = TaskTraceContext.Create(
                instanceId: Guid.NewGuid(),
                domain: "core",
                workflowKey: "money-transfer",
                workflowVersion: "1.0.2",
                correlationId: Guid.NewGuid().ToString("N"),
                headers: null,
                instanceDataJson: null,
                traceParent: null,
                traceState: null,
                sub: "user-1",
                actSub: null,
                requestId: "req-1")
        };

        var bytes = TaskInvokePayload.Serialize(request);
        var back = TaskInvokePayload.Deserialize<TaskInvokeRequest>(bytes);

        back.Envelope.TaskKey.ShouldBe("get-iban-history");
        back.Envelope.Binding.GetProperty("url").GetString().ShouldBe("https://x");
        back.TraceContext!.Domain.ShouldBe("core");
    }

    [Fact]
    public void Serialize_UsesCamelCase_MatchingTheHttpEndpoint()
    {
        var response = new TaskInvokeResponse { Success = true, ExecutionDurationMs = 42 };

        var json = TaskInvokePayload.Serialize(response).ToStringUtf8();

        json.ShouldContain("\"success\":true");     // camelCase, not PascalCase
        json.ShouldContain("\"executionDurationMs\":42");
    }
}
```

> NOTE for the implementer: `TaskEnvelope` / `TaskTraceContext.Create` signatures above
> are best-effort from the abstractions project — open
> `src/BBT.Workflow.Execution.Abstractions/TaskEnvelope.cs` and adjust the object
> construction to the real required members. The ASSERTIONS are the contract; the
> construction is scaffolding.

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskInvokePayloadTests" --nologo -v q`
Expected: FAIL — `TaskInvokePayload` does not exist (compile error).

- [ ] **Step 5: Implement the serializer**

`src/BBT.Workflow.Execution.Abstractions/Grpc/TaskInvokePayload.cs`:

```csharp
using System.Text.Json;
using Google.Protobuf;

namespace BBT.Workflow.Execution.Grpc;

/// <summary>
/// Serializes the task-invocation DTOs into the gRPC payload bytes and back.
/// <para>
/// One place on purpose: the gRPC transport must exchange exactly the JSON the HTTP
/// endpoint exchanges (ASP.NET Core web defaults — camelCase, case-insensitive read),
/// so the serializer options live here once instead of at each call site where they
/// could drift apart.
/// </para>
/// </summary>
public static class TaskInvokePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes <paramref name="value"/> to the UTF-8 JSON payload bytes.</summary>
    public static ByteString Serialize<T>(T value)
        => ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value, Options));

    /// <summary>Deserializes the payload bytes produced by <see cref="Serialize{T}"/>.</summary>
    public static T Deserialize<T>(ByteString payload)
        => JsonSerializer.Deserialize<T>(payload.Span, Options)!;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskInvokePayloadTests" --nologo -v q`
Expected: PASS (2 tests). Also run `dotnet build vnext.sln -v q --nologo` — 0 errors (proves the proto codegen and new packages don't break any referencing project).

- [ ] **Step 7: Commit**

```bash
git add src/BBT.Workflow.Execution.Abstractions/ test/BBT.Workflow.Application.Tests/Execution/TaskInvokePayloadTests.cs Directory.Build.props
git commit -m "feat(execution): TaskInvoker proto contract and JSON payload serializer"
```

---

### Task 3: Extract the shared invoke handler in the Execution host

**Files:**
- Create: `execution/BBT.Workflow.Execution.HttpApi.Host/Services/TaskInvokeHandler.cs`
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/Controllers/Executions/ExecutionController.cs`
- Modify: the host's DI registration (`execution/BBT.Workflow.Execution.HttpApi.Host/Microsoft/Extensions/DependencyInjection/ExecutionApiServiceCollectionExtensions.cs`) — add `services.AddScoped<TaskInvokeHandler>();`
- Test: `test/BBT.Workflow.Application.Tests/Execution/TaskInvokeHandlerTests.cs` *(only if the host project is referenced by a test project — check; if no test project references the host, the controller-parity risk is covered by Task 6's end-to-end run instead, and this task carries no unit test)*

**Interfaces:**
- Consumes: `ITaskInvokerRegistry.InvokeAsync(TaskEnvelope, CancellationToken)` (existing), `TaskInvokeRequest`/`TaskInvokeResponse` (existing).
- Produces: `sealed class TaskInvokeHandler` with:
  - `Task<TaskInvokeResponse> HandleAsync(TaskInvokeRequest request, CancellationToken ct)`

- [ ] **Step 1: Move the controller body into the handler**

Create `TaskInvokeHandler` and move EVERYTHING between the controller's parameter unpacking and the final `Ok(...)` into it, unchanged: identity-claim normalization, `RestoreActivityFromBodyIfDetached` (move the private method too), all `activity?.SetTag/SetBaggage` calls, the logger scope with its explanatory comment about sub/act_sub, the `invokerRegistry.InvokeAsync` call, and construction of `TaskInvokeResponse`. The class takes `ITaskInvokerRegistry` and `ILogger<TaskInvokeHandler>` via primary constructor. The method returns the `TaskInvokeResponse` (not an `IActionResult`).

The controller becomes:

```csharp
public async Task<IActionResult> InvokeTaskAsync(
    [FromRoute] string type,
    [FromRoute] string key,
    [FromBody] TaskInvokeRequest request,
    CancellationToken cancellationToken = default)
    => Ok(await handler.HandleAsync(request, cancellationToken));
```

with `TaskInvokeHandler handler` added to the controller's constructor. Note the current controller ignores the `type`/`key` route values beyond routing (the envelope carries them) — preserve that: the handler reads them from `request.Envelope`.

- [ ] **Step 2: Build and run the existing suite**

Run: `dotnet build vnext.sln -v q --nologo` → 0 errors.
Run: `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` → failing-name set unchanged vs baseline (this is a pure move; any new failure means behavior drifted — fix before proceeding).

- [ ] **Step 3: Commit**

```bash
git add execution/ test/
git commit -m "refactor(execution): extract TaskInvokeHandler so HTTP and gRPC share one invoke path"
```

---

### Task 4: gRPC service + Kestrel dual-protocol in the Execution host

**Files:**
- Create: `execution/BBT.Workflow.Execution.HttpApi.Host/Services/TaskInvokerGrpcService.cs`
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/BBT.Workflow.Execution.HttpApi.Host.csproj` (add `Grpc.AspNetCore`)
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/Microsoft/Extensions/DependencyInjection/ExecutionApiServiceCollectionExtensions.cs` (AddGrpc)
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/Microsoft/AspNetCore/Builder/ExecutionApiApplicationBuilderExtensions.cs` (MapGrpcService)
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json` (Kestrel protocols)

**Interfaces:**
- Consumes: `TaskInvoker.TaskInvokerBase` + `InvokeRequest`/`InvokeReply` + `TaskInvokePayload` (Task 2), `TaskInvokeHandler` (Task 3).
- Produces: gRPC service `bbt.workflow.execution.v1.TaskInvoker/Invoke` served on the app port.

- [ ] **Step 1: Add the package and registrations**

Csproj: `<PackageReference Include="Grpc.AspNetCore" Version="$(GrpcAspNetCorePackageVersion)" />` (pin in `Directory.Build.props` like Task 2's packages).

In the service collection extension (next to the existing MVC registration):

```csharp
services.AddGrpc(options =>
{
    // Aligned with the sidecar's http-max-request-size: "64" (MB). Task payloads carry
    // full instance data and can be large; the default 4 MB receive cap would fail them.
    options.MaxReceiveMessageSize = 64 * 1024 * 1024;
    options.MaxSendMessageSize = 64 * 1024 * 1024;
});
```

In the application builder extension, next to `MapSubscribeHandler`:

```csharp
app.MapGrpcService<TaskInvokerGrpcService>();
```

- [ ] **Step 2: Write the service**

```csharp
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Grpc;
using Grpc.Core;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// gRPC surface of the task-invoke endpoint, served over Dapr gRPC proxy mode.
/// <para>
/// A thin shell by design: transport decode → <see cref="TaskInvokeHandler"/> → transport
/// encode. Everything with behavior (activity enrichment, trace restore, the registry
/// call) lives in the shared handler so the HTTP controller and this service cannot
/// drift apart.
/// </para>
/// </summary>
public sealed class TaskInvokerGrpcService(TaskInvokeHandler handler)
    : TaskInvoker.TaskInvokerBase
{
    public override async Task<InvokeReply> Invoke(InvokeRequest request, ServerCallContext context)
    {
        var invokeRequest = TaskInvokePayload.Deserialize<TaskInvokeRequest>(request.PayloadJson);
        var response = await handler.HandleAsync(invokeRequest, context.CancellationToken);
        return new InvokeReply { PayloadJson = TaskInvokePayload.Serialize(response) };
    }
}
```

(Adjust the namespace/using of `TaskInvokeHandler` to wherever Task 3 actually placed it.)

- [ ] **Step 3: Enable h2c alongside HTTP/1.1**

In `execution/.../appsettings.json`, extend the EXISTING `"Kestrel"` object (keep the `Limits` block as-is):

```json
"Kestrel": {
  "EndpointDefaults": {
    "Protocols": "Http1AndHttp2"
  },
  "Limits": {
    "MaxRequestHeadersTotalSize": 65536,
    "MaxRequestHeaderCount": 200,
    "MaxRequestBodySize": 10485760
  }
}
```

HTTP/1.1 traffic (probes, swagger, the HTTP controller) is unaffected; gRPC clients connect with HTTP/2 prior knowledge on the same cleartext port.

- [ ] **Step 4: Build + smoke the host boots**

Run: `dotnet build vnext.sln -v q --nologo` → 0 errors.
Run the host briefly: `dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http --no-build` → `GET http://localhost:4202/health` returns 200 (HTTP/1.1 still served). Stop it.

- [ ] **Step 5: Commit**

```bash
git add execution/ Directory.Build.props
git commit -m "feat(execution): serve TaskInvoker over gRPC on the app port alongside HTTP"
```

---

### Task 5: gRPC client path in RemoteInvokerService behind ExecutionApi:Transport

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/Executors/Remote/RemoteInvokerService.cs`
- Modify: `src/BBT.Workflow.Application/BBT.Workflow.Application.csproj` (no new package needed — `Dapr.Client` and the Abstractions reference already provide `CreateInvocationInvoker` and the generated client; verify and only add `Grpc.Net.Client` explicitly if the transitive reference doesn't surface `GrpcChannelOptions`)
- Test: `test/BBT.Workflow.Application.Tests/Tasks/RemoteInvokerGrpcErrorMappingTests.cs`

**Interfaces:**
- Consumes: `TaskInvoker.TaskInvokerClient`, `TaskInvokePayload` (Task 2); existing `TaskInvocationResult.Failure(error, statusCode, executionDurationMs, taskType)`.
- Produces: `RemoteInvokerService` honoring `ExecutionApi:Transport` (`"http"` default | `"grpc"`); internal static `MapRpcFailure(RpcException, long elapsedMs, string taskType, int timeoutSeconds)` for testability.

- [ ] **Step 1: Write the failing error-mapping tests**

```csharp
using BBT.Workflow.Tasks.Executors;
using Grpc.Core;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks;

/// <summary>
/// Pins gRPC transport failures to the SAME TaskInvocationResult shapes the HTTP path
/// produces, so the error boundary sees one contract regardless of transport.
/// </summary>
public sealed class RemoteInvokerGrpcErrorMappingTests
{
    [Fact]
    public void DeadlineExceeded_MapsToTheSame408TheHttpTimeoutProduces()
    {
        var ex = new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline"));

        var result = RemoteInvokerService.MapRpcFailure(ex, elapsedMs: 60_000, taskType: "3", timeoutSeconds: 60);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(408);
        result.ErrorMessage.ShouldContain("60");
        result.TaskType.ShouldBe("3");
    }

    [Fact]
    public void Unavailable_MapsToTheSame500AnyTransportFailureProduces()
    {
        var ex = new RpcException(new Status(StatusCode.Unavailable, "connection refused"));

        var result = RemoteInvokerService.MapRpcFailure(ex, elapsedMs: 12, taskType: "3", timeoutSeconds: 60);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        result.ErrorMessage.ShouldContain("connection refused");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~RemoteInvokerGrpcErrorMappingTests" --nologo -v q`
Expected: FAIL — `MapRpcFailure` not defined.

- [ ] **Step 3: Implement the gRPC path**

In `RemoteInvokerService`:

1. New fields + ctor reads (keep existing ones untouched):

```csharp
private readonly bool _useGrpcTransport;
private readonly Lazy<TaskInvoker.TaskInvokerClient> _grpcClient;
```

```csharp
_useGrpcTransport = string.Equals(
    configuration["ExecutionApi:Transport"], "grpc", StringComparison.OrdinalIgnoreCase);
_grpcClient = new Lazy<TaskInvoker.TaskInvokerClient>(() =>
    new TaskInvoker.TaskInvokerClient(DaprClient.CreateInvocationInvoker(
        _executionServiceAppId,
        grpcChannelOptions: new GrpcChannelOptions
        {
            // Aligned with the sidecar's http-max-request-size: "64" (MB) and the
            // server's AddGrpc limits — the three must agree or large payloads fail
            // on whichever hop has the smallest cap.
            MaxReceiveMessageSize = 64 * 1024 * 1024,
            MaxSendMessageSize = 64 * 1024 * 1024
        })));
```

Lazy on purpose: `CreateInvocationInvoker` opens a channel to the sidecar; environments running `Transport=http` (the default) must not pay for or depend on it.

2. At the top of `InvokeAsync`, after building `request`, branch:

```csharp
if (_useGrpcTransport)
    return await InvokeOverGrpcAsync(taskType, taskKey, request, traceContext, stopwatch, invocationCts.Token, cancellationToken);
```

3. The gRPC method — same result contract, same timeout semantics, no retry:

```csharp
private async Task<Result<TaskInvocationResult>> InvokeOverGrpcAsync(
    string taskType,
    string taskKey,
    TaskInvokeRequest request,
    TaskTraceContext traceContext,
    Stopwatch stopwatch,
    CancellationToken invocationToken,
    CancellationToken parentToken)
{
    try
    {
        var metadata = new Metadata
        {
            { WorkflowInfo.Name, WorkflowInfo.Generate(
                traceContext.Domain ?? "unknown",
                traceContext.WorkflowKey ?? "unknown",
                traceContext.WorkflowVersion ?? "latest",
                traceContext.InstanceId) }
        };
        // Same context keys the HTTP path sends as headers — gRPC metadata ARE HTTP/2
        // headers on the wire, so Execution's header-driven enrichers keep working.
        if (traceContext.InstanceId != Guid.Empty)
            metadata.Add(TelemetryConstants.HeaderNames.WorkflowInstanceId,
                traceContext.InstanceId.ToString("D").ToLowerInvariant());
        if (Guid.TryParseExact(traceContext.CorrelationId, "N", out var correlationId)
            && correlationId != Guid.Empty)
            metadata.Add(TelemetryConstants.HeaderNames.CorrelationId, correlationId.ToString("N"));
        if (TelemetryConstants.TryNormalizeIdentityClaim(traceContext.Sub, out var subject))
            metadata.Add(TelemetryConstants.HeaderNames.Sub, subject);
        if (TelemetryConstants.TryNormalizeIdentityClaim(traceContext.ActSub, out var actSub))
            metadata.Add(TelemetryConstants.HeaderNames.ActSub, actSub);
        var rootIdBaggage = Activity.Current?.GetBaggageItem(TelemetryConstants.TagNames.RootInstanceId);
        if (!string.IsNullOrEmpty(rootIdBaggage))
            metadata.Add(TelemetryConstants.HeaderNames.RootInstanceId, rootIdBaggage);
        if (!string.IsNullOrEmpty(traceContext.RequestId))
            metadata.Add(TelemetryConstants.HeaderNames.RequestId, traceContext.RequestId);

        var reply = await _grpcClient.Value.InvokeAsync(
            new InvokeRequest
            {
                TaskType = taskType,
                TaskKey = taskKey,
                PayloadJson = TaskInvokePayload.Serialize(request)
            },
            new CallOptions(
                headers: metadata,
                deadline: DateTime.UtcNow.AddSeconds(_invocationTimeoutSeconds),
                cancellationToken: invocationToken));

        stopwatch.Stop();
        var response = TaskInvokePayload.Deserialize<TaskInvokeResponse>(reply.PayloadJson);

        return Result<TaskInvocationResult>.Ok(new TaskInvocationResult
        {
            IsSuccess = response.Result!.IsSuccess,
            StatusCode = response.Result.StatusCode,
            Body = response.Result.Body,
            Data = response.Result.Data,
            ErrorMessage = response.Result.ErrorMessage,
            Headers = response.Result.Headers,
            TaskType = response.Result.TaskType,
            Metadata = response.Result.Metadata,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds
        });
    }
    catch (RpcException ex) when (parentToken.IsCancellationRequested)
    {
        stopwatch.Stop();
        _ = ex;
        throw new OperationCanceledException(parentToken); // parent cancellation wins, same as HTTP path
    }
    catch (RpcException ex)
    {
        stopwatch.Stop();
        _logger.LogError("gRPC invocation of task {TaskKey} failed: {Status} {Detail}",
            taskKey, ex.StatusCode, ex.Status.Detail);
        return Result<TaskInvocationResult>.Ok(
            MapRpcFailure(ex, stopwatch.ElapsedMilliseconds, taskType, _invocationTimeoutSeconds));
    }
    catch (OperationCanceledException) when (!parentToken.IsCancellationRequested)
    {
        stopwatch.Stop();
        _logger.LogError(
            "gRPC invocation timeout after {Seconds}s. TaskType: {TaskType}, TaskKey: {TaskKey} [timeout.layer=remote]",
            _invocationTimeoutSeconds, taskType, taskKey);
        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
            error: $"Dapr invocation timeout after {_invocationTimeoutSeconds}s",
            statusCode: 408,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: taskType));
    }
}

internal static TaskInvocationResult MapRpcFailure(
    RpcException ex, long elapsedMs, string taskType, int timeoutSeconds)
    => ex.StatusCode == StatusCode.DeadlineExceeded
        ? TaskInvocationResult.Failure(
            error: $"Dapr invocation timeout after {timeoutSeconds}s",
            statusCode: 408,
            executionDurationMs: elapsedMs,
            taskType: taskType)
        : TaskInvocationResult.Failure(
            error: $"{ex.StatusCode}: {ex.Status.Detail}",
            statusCode: 500,
            executionDurationMs: elapsedMs,
            taskType: taskType);
```

Usings to add: `Dapr.Client` (already), `Grpc.Core`, `Grpc.Net.Client`, `BBT.Workflow.Execution.Grpc`.

> NOTE: `TaskInvocationResult` construction above mirrors the existing HTTP-path mapping
> in this same file — copy the exact property list from there if it differs.

- [ ] **Step 4: Run the tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~RemoteInvokerGrpcErrorMappingTests" --nologo -v q`
Expected: PASS. Then `dotnet build vnext.sln -v q --nologo` → 0 errors, and the full Application.Tests run → failing-name set unchanged vs baseline.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/ test/
git commit -m "feat(orchestration): gRPC proxy-mode transport for Execution invokes behind ExecutionApi:Transport"
```

---

### Task 6: Flip local config to gRPC + end-to-end verification + rollback proof

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json` (`ExecutionApi` section: add `"Transport": "grpc"`)
- Modify: `etc/docker/docker-compose.yml` — `vnext-execution-dapr` command: add `"--app-protocol", "grpc"` (default is http; the flag pairs with the appsettings flip — flipping only one breaks invocation, see spec)
- Modify: `docs/runtime/dapr-invocation-transport.md` — fill the verification section with the observed trace ids/spans

**Interfaces:**
- Consumes: everything from Tasks 2–5.
- Produces: a verified gRPC path and a verified rollback.

- [ ] **Step 1: Flip both switches**

`appsettings.json`:

```json
"ExecutionApi": {
  "AppId": "vnext-execution-app",
  "InvocationTimeoutSeconds": 60,
  "Transport": "grpc"
},
```

`docker-compose.yml` (`vnext-execution-dapr` → `command`): add two array items after `"--app-port", "4202"`:

```yaml
      "--app-protocol", "grpc",
```

- [ ] **Step 2: Restart the stack and run one flow**

```bash
cd etc/docker && docker compose up -d vnext-execution-dapr
```

Then (root of vnext) rebuild + start the four apps with `--launch-profile http` (orchestration 4201, execution 4202, inbox, outbox — sequential `dotnet build vnext.sln` first; PostSharp fails on parallel first-builds). Then from vnext-example:

```bash
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings --filter "FullyQualifiedName~MoneyTransferTests" --nologo -v q
```

Expected: `Passed! 5/5` — MoneyTransfer exercises DaprService tasks through Execution (`Invoke.{type}/{key}` spans exist on the Execution side).

- [ ] **Step 3: Verify the trace shape in Elastic**

Query the newest `TransitionJob.Execute/*` trace (localhost:9200, `.ds-traces-apm*`). Assert:
- orchestration has a client span named `bbt.workflow.execution.v1.TaskInvoker/Invoke` (gRPC client instrumentation) where the old `POST .../invoke/{type}/{key}` HttpClient span used to be, parented under `Task.Invoke`;
- the sidecar `CallLocal/vnext-execution-app/...` span still appears and is parented under it;
- Execution's server transaction is the gRPC method (no longer `POST api/v{version}/execution/invoke/{type}/{key}`), and `Invoke.{taskType}/{taskKey}` spans hang beneath it;
- no orphaned spans introduced (compare orphan count with a pre-flip trace of the same flow).

Record the trace id and paste the observed tree into `docs/runtime/dapr-invocation-transport.md` § verification.

- [ ] **Step 4: Rollback proof**

Revert the two flips ONLY in the running environment (set `"Transport": "http"`, remove `--app-protocol`), restart execution sidecar + orchestration app, rerun the same MoneyTransfer filter → `Passed! 5/5`. Then restore the flips to `grpc` (the committed state keeps gRPC on for local dev). This proves the rollback story is config-only in both directions.

- [ ] **Step 5: Commit**

```bash
git add orchestration/ etc/docker/docker-compose.yml docs/runtime/dapr-invocation-transport.md
git commit -m "feat(transport): run orchestration→execution over Dapr gRPC proxy mode locally"
```

---

### Task 7: Helm chart — the deployment-side switch (vnext-helm-charts repo, local branch)

**Files (in `/Users/U0B006/Documents/repos/burgan-tech/vnext-helm-charts`, branch `docs/dapr-app-protocol-clarify` or a new local branch off it):**
- Modify: `charts/vnext/values.yaml` — execution component's `dapr` block: `protocol: "grpc"`; orchestrator env: `ExecutionApi__Transport: "grpc"` (via the chart's env-var mechanism for the orchestrator container — follow how existing `ExecutionApi`/app config flows into env; if config flows via appsettings only, add the env var to the orchestrator deployment env list)
- Modify: `charts/vnext/values.yaml` — global `dapr.protocol` comment gains one line: execution overrides to `grpc` deliberately; the OTHER components MUST stay `http` (they host HTTP-delivered pub/sub and jobs)

**Interfaces:**
- Consumes: the chart's per-component `dapr` blocks (`.Values.execution.dapr` feeding `vnext.daprAnnotations`).
- Produces: rendered Execution pod annotation `dapr.io/app-protocol: "grpc"` + orchestrator env `ExecutionApi__Transport=grpc`.

- [ ] **Step 1: Locate the execution `dapr` block and the orchestrator env mechanism**

Read `charts/vnext/values.yaml` (execution section) and `charts/vnext/templates/execution/deployment.yaml` + `templates/orchestrator/deployment.yaml`. Confirm `.Values.execution.dapr.protocol` reaches `vnext.daprAnnotations` (helper reads `.dapr.protocol`, default `http`).

- [ ] **Step 2: Make the two edits with WHY-comments**

On the execution `dapr` block:

```yaml
    # gRPC deliberately, and only here: Execution's whole inbound surface is the
    # TaskInvoker gRPC service (Dapr proxy mode). The other components stay "http" —
    # they receive pub/sub and job callbacks over the HTTP surface, which app-protocol
    # grpc would silently break. Pairs with ExecutionApi__Transport=grpc on the
    # orchestrator; flipping only one of the two breaks task invocation.
    protocol: "grpc"
```

- [ ] **Step 3: Lint and render-check**

Run: `helm lint charts/vnext` → 0 failed.
Run: `helm template charts/vnext 2>/dev/null | grep -B2 -A1 'app-protocol'` → execution pod shows `grpc`, every other component shows `http`.

- [ ] **Step 4: Commit (local only, do NOT push)**

```bash
git add charts/vnext/values.yaml
git commit -m "feat(execution): app-protocol grpc for the TaskInvoker proxy-mode surface"
```

---

## Self-Review (done at planning time)

- **Spec coverage:** Decision 1 → Task 1; contract → Task 2; dual server surface → Tasks 3–4; client + switch → Task 5; success criteria 1–3 → Task 6; deployment switch → Task 7. Deferred item (other transports) intentionally has no task.
- **Known soft spots, stated rather than hidden:** (a) `TaskEnvelope`/`TaskTraceContext.Create` construction in Task 2's test is scaffolding to be adjusted against the real types; (b) Task 3's unit test existence depends on whether any test project references the host — the fallback gate is Task 6's E2E; (c) exact Grpc.* package versions are pinned at implementation time by what restores on net10.0, in `Directory.Build.props`, never inline.
- **Type consistency:** `TaskInvokePayload.Serialize/Deserialize` (Task 2) used in Tasks 4–5; `TaskInvokeHandler.HandleAsync` (Task 3) used in Task 4; `MapRpcFailure` defined and tested in Task 5; proto names `TaskInvoker`/`InvokeRequest`/`InvokeReply` consistent across 2/4/5/6.
