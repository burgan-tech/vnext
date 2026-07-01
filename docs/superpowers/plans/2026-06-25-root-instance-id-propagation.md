# Root Instance ID Propagation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enrich all logs and OpenTelemetry traces with the root (ancestor) flow's instance ID across a deep subflow chain (A→B→C→D), so searching by A's instance ID surfaces every span and log line in the entire hierarchy.

**Architecture:** A new `root.instance.id` key is persisted in each subflow instance's `ExtraProperties` at creation time. The value is always A's ID — computed in `SubflowStarter` as "parent's root ID, or parent's own ID if parent has no root". This key is also forwarded in the `X-Root-Instance-Id` HTTP header on every cross-domain subflow start, where the `ParentInstanceIdEnrichmentMiddleware` picks it up and stamps it onto the Activity and log scope for that request. The pipeline executor stamps it on every subsequent pipeline run by reading it from `ExtraProperties`.

**Tech Stack:** C# 10+, ASP.NET Core middleware, System.Diagnostics.Activity (OpenTelemetry), `Microsoft.Extensions.Logging` structured scopes, Aether `ExtraPropertyDictionary`.

---

## File Map

| File | Change |
|------|--------|
| `src/BBT.Workflow.Domain/DomainConsts.cs` | Add `MetaDataKeys.RootInstanceId` constant |
| `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` | Add `TagNames.RootInstanceId` + `HeaderNames.RootInstanceId` |
| `src/BBT.Workflow.Domain/Instances/InstanceMetadataExtensions.cs` | Add `GetRootInstanceId()` extension |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs` | Propagate root ID into ExtraProperties, headers, tags, log scope, and activity |
| `src/BBT.Workflow.HttpApi.Shared/Middlewares/ParentInstanceIdEnrichmentMiddleware.cs` | Also read `X-Root-Instance-Id` header |
| `src/BBT.Workflow.Application/SubFlow/Services/SubFlowActivityHelper.cs` | Add `rootInstanceId` param to `EnrichWithStart` |
| `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs` | Stamp root ID in `BuildLogScope` and `EnrichTelemetry` |
| `src/BBT.Workflow.Application/Execution/PostCommit/Handlers/StartSubflowJobHandler.cs` | Add root ID to log scope |
| `test/BBT.Workflow.Domain.Tests/Instances/InstanceMetadataExtensionsTests.cs` | Unit tests for `GetRootInstanceId()` |

---

## Task 1: Add root instance ID constants

**Files:**
- Modify: `src/BBT.Workflow.Domain/DomainConsts.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs`

- [ ] **Step 1.1: Add `MetaDataKeys.RootInstanceId` to DomainConsts**

In `DomainConsts.cs`, inside the `MetaDataKeys` class after the last existing constant:

```csharp
public const string RootInstanceId = "root.instance.id";
```

Full `MetaDataKeys` after the change:
```csharp
public class MetaDataKeys
{
    public const string Id = "parent.id";
    public const string Key = "parent.key";
    public const string Domain = "parent.domain";
    public const string Flow = "parent.flow";
    public const string Version = "parent.version";
    public const string State = "parent.state";
    public const string FlowType = "parent.flowtype";
    public const string Transition = "parent.transition";
    public const string Sync = "sync";
    public const string Callback = "callback";
    public const string TimeoutOverride = "subflow.timeout_override";
    public const string TransitionRoleOverrides = "subflow.transition_role_overrides";
    public const string StateRoleOverrides = "subflow.state_role_overrides";
    /// <summary>Root (ancestor) flow instance ID — always carries the original A-flow ID down the entire chain.</summary>
    public const string RootInstanceId = "root.instance.id";
}
```

- [ ] **Step 1.2: Add `TagNames.RootInstanceId` and `HeaderNames.RootInstanceId` to TelemetryConstants**

In `TelemetryConstants.cs`, add to `TagNames` after `SubflowInstanceId`:
```csharp
/// <summary>
/// Root (ancestor) instance ID — the top-level flow in a nested subflow chain (A→B→C→D always carries A's ID).
/// </summary>
public const string RootInstanceId = "vnext.root.instance.id";
```

Add to `HeaderNames` after `ParentInstanceId`:
```csharp
/// <summary>
/// Request header carrying the root (ancestor) instance ID across the full subflow chain.
/// Remains constant at A's ID regardless of nesting depth.
/// </summary>
public const string RootInstanceId = "X-Root-Instance-Id";
```

- [ ] **Step 1.3: Build to confirm no compilation errors**

```bash
dotnet build src/BBT.Workflow.Domain/BBT.Workflow.Domain.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 1.4: Commit**

```bash
git add src/BBT.Workflow.Domain/DomainConsts.cs \
        src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs
git commit -m "feat(telemetry): add RootInstanceId constants for deep subflow chain correlation"
```

---

## Task 2: Add `GetRootInstanceId()` extension

**Files:**
- Modify: `src/BBT.Workflow.Domain/Instances/InstanceMetadataExtensions.cs`

This helper encapsulates the "root or self" fallback logic used in multiple call sites.

- [ ] **Step 2.1: Add extension method**

Append to the end of the static class in `InstanceMetadataExtensions.cs` (before the closing `}`):

```csharp
/// <summary>
/// Returns the root (ancestor) instance ID stored in <see cref="DomainConsts.MetaDataKeys.RootInstanceId"/>.
/// If the key is absent (i.e. this instance IS the root), returns the instance's own <see cref="Instance.Id"/>.
/// </summary>
public static Guid GetRootInstanceId(this Instance instance)
{
    if (instance.ExtraProperties != null
        && instance.ExtraProperties.TryGetValue(DomainConsts.MetaDataKeys.RootInstanceId, out var raw)
        && raw != null
        && Guid.TryParse(raw.ToString(), out var rootId)
        && rootId != Guid.Empty)
    {
        return rootId;
    }

    return instance.Id;
}
```

- [ ] **Step 2.2: Build Domain project**

```bash
dotnet build src/BBT.Workflow.Domain/BBT.Workflow.Domain.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 2.3: Write failing unit tests**

Create `test/BBT.Workflow.Domain.Tests/Instances/InstanceMetadataExtensionsTests.cs`:

```csharp
using System;
using BBT.Aether;
using Xunit;

namespace BBT.Workflow.Instances;

public class InstanceMetadataExtensionsTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void GetRootInstanceId_WhenNoRootKeyInExtraProperties_ReturnsSelfId()
    {
        // Arrange — root instance (A): no parent, no root.instance.id
        var id = Guid.NewGuid();
        var instance = Instance.Create(id, "flow-a", "1.0.0", "key-a");
        // ExtraProperties is empty by default

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(id, result);
    }

    [Fact]
    public void GetRootInstanceId_WhenRootKeyPresent_ReturnsStoredRootId()
    {
        // Arrange — subflow instance (C): has root.instance.id = A's ID
        var instanceId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow-c", "1.0.0", "key-c");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.RootInstanceId] = rootId
        });

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(rootId, result);
    }

    [Fact]
    public void GetRootInstanceId_WhenRootKeyStoredAsString_ParsesAndReturnsRootId()
    {
        // Arrange — ExtraPropertyDictionary may round-trip values as strings after deserialization
        var instanceId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow-b", "1.0.0", "key-b");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.RootInstanceId] = rootId.ToString()
        });

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(rootId, result);
    }
}
```

- [ ] **Step 2.4: Run tests — expect failure (method not found until build)**

```bash
dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~InstanceMetadataExtensionsTests" -v minimal
```

Expected: 3 tests pass (method was just added in 2.1).

- [ ] **Step 2.5: Commit**

```bash
git add src/BBT.Workflow.Domain/Instances/InstanceMetadataExtensions.cs \
        test/BBT.Workflow.Domain.Tests/Instances/InstanceMetadataExtensionsTests.cs
git commit -m "feat(domain): add GetRootInstanceId extension for subflow chain ancestor lookup"
```

---

## Task 3: Propagate root ID in SubflowStarter

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs`

The core propagation: B starts C with A's root ID by looking up `parentInstance.GetRootInstanceId()`.

- [ ] **Step 3.1: Compute root instance ID before building `createInstanceInput`**

In `SubflowStarter.StartAsync`, locate the `using var activity = SubFlowActivityHelper.StartActivity(...)` block (around line 90). Just before `SubFlowActivityHelper.EnrichWithStart(...)`, add:

```csharp
// Propagate root instance ID: use parent's stored root, or parent itself if parent is the root
var rootInstanceId = parentInstance.GetRootInstanceId();
```

- [ ] **Step 3.2: Pass root ID to `EnrichWithStart`**

The existing call:
```csharp
SubFlowActivityHelper.EnrichWithStart(
    activity,
    parentInstance.Id,
    subFlowReference.Domain,
    subFlowReference.Key,
    correlation.SubFlowInstanceId);
```

Change to:
```csharp
SubFlowActivityHelper.EnrichWithStart(
    activity,
    parentInstance.Id,
    subFlowReference.Domain,
    subFlowReference.Key,
    correlation.SubFlowInstanceId,
    rootInstanceId);
```

- [ ] **Step 3.3: Add root ID to `logger.BeginScope`**

Locate the `using (logger.BeginScope(new Dictionary<string, object> { ... }))` block. Add the root ID entry:

```csharp
using (logger.BeginScope(new Dictionary<string, object>
{
    [TelemetryConstants.TagNames.Domain] = workflow.Domain,
    [TelemetryConstants.TagNames.Flow] = workflow.Key,
    [TelemetryConstants.TagNames.FlowVersion] = workflow.Version,
    [TelemetryConstants.TagNames.InstanceId] = parentInstance.Id,
    [TelemetryConstants.TagNames.InstanceKey] = parentInstance.Key ?? "N/A",
    [TelemetryConstants.TagNames.SubflowInstanceId] = correlation.SubFlowInstanceId,
    [TelemetryConstants.TagNames.RootInstanceId] = rootInstanceId   // NEW
}))
```

- [ ] **Step 3.4: Add root ID to `createInstanceInput.ExtraProperties`**

Inside the `ExtraProperties = new ExtraPropertyDictionary { ... }` initialiser, add after the existing entries:

```csharp
ExtraProperties = new ExtraPropertyDictionary
{
    [DomainConsts.MetaDataKeys.Id] = parentInstance.Id,
    [DomainConsts.MetaDataKeys.Key] = parentInstance.Key ?? string.Empty,
    [DomainConsts.MetaDataKeys.Domain] = workflow.Domain,
    [DomainConsts.MetaDataKeys.Flow] = workflow.Key,
    [DomainConsts.MetaDataKeys.Version] = workflow.Version,
    [DomainConsts.MetaDataKeys.State] = stateKey,
    [DomainConsts.MetaDataKeys.Transition] = transitionKey,
    [DomainConsts.MetaDataKeys.FlowType] = subFlowTypeCode,
    [DomainConsts.MetaDataKeys.RootInstanceId] = rootInstanceId     // NEW
},
```

- [ ] **Step 3.5: Add root ID to `Tags`**

```csharp
Tags =
[
    $"parent.key:{parentInstance.Key}",
    $"parent.domain:{workflow.Domain}",
    $"parent.flow:{workflow.Key}",
    $"root.instance:{rootInstanceId}"   // NEW
],
```

- [ ] **Step 3.6: Forward root ID in the outgoing HTTP headers**

Locate:
```csharp
headers[TelemetryConstants.HeaderNames.ParentInstanceId] = parentInstance.Id.ToString();
```

Add immediately after:
```csharp
headers[TelemetryConstants.HeaderNames.RootInstanceId] = rootInstanceId.ToString();  // NEW
```

- [ ] **Step 3.7: Build Application project**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)` (will fail at Step 3.2 until Task 4 adds the `rootInstanceId` parameter — complete Task 4 first if build fails).

- [ ] **Step 3.8: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs
git commit -m "feat(subflow): propagate root instance ID through ExtraProperties, headers, and log scope"
```

---

## Task 4: Add `rootInstanceId` parameter to `SubFlowActivityHelper.EnrichWithStart`

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubFlowActivityHelper.cs`

- [ ] **Step 4.1: Extend `EnrichWithStart` signature**

Current signature (line 67):
```csharp
public static void EnrichWithStart(
    Activity? activity,
    Guid parentInstanceId,
    string subFlowDomain,
    string subFlowKey,
    Guid subFlowInstanceId)
{
    if (activity is null) return;

    activity.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstanceId);
    activity.SetTag("vnext.subflow.domain", subFlowDomain);
    activity.SetTag("vnext.subflow.flow", subFlowKey);
    activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subFlowInstanceId);
```

Replace with (add `rootInstanceId` param with default to avoid breaking other callers):
```csharp
public static void EnrichWithStart(
    Activity? activity,
    Guid parentInstanceId,
    string subFlowDomain,
    string subFlowKey,
    Guid subFlowInstanceId,
    Guid rootInstanceId = default)
{
    if (activity is null) return;

    activity.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstanceId);
    activity.SetTag("vnext.subflow.domain", subFlowDomain);
    activity.SetTag("vnext.subflow.flow", subFlowKey);
    activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subFlowInstanceId);
    if (rootInstanceId != default)
    {
        activity.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId.ToString());
        activity.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId.ToString());
    }
```

- [ ] **Step 4.2: Build**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4.3: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubFlowActivityHelper.cs
git commit -m "feat(telemetry): add rootInstanceId to SubFlowActivityHelper.EnrichWithStart"
```

---

## Task 5: Extend `ParentInstanceIdEnrichmentMiddleware` to handle `X-Root-Instance-Id`

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/Middlewares/ParentInstanceIdEnrichmentMiddleware.cs`

This stamps the root ID onto the Activity and log scope for the **incoming start/transition request** for a subflow instance, before the pipeline even runs.

- [ ] **Step 5.1: Rewrite `InvokeAsync`**

Replace the entire `InvokeAsync` method body:

```csharp
/// <summary>
/// Reads the parent and root instance ID headers, enriches Activity and log scope when present,
/// then invokes the next middleware.
/// </summary>
public async Task InvokeAsync(HttpContext context)
{
    var parentInstanceId = context.Request.Headers[TelemetryConstants.HeaderNames.ParentInstanceId].FirstOrDefault();
    var rootInstanceId   = context.Request.Headers[TelemetryConstants.HeaderNames.RootInstanceId].FirstOrDefault();

    var activity = Activity.Current;
    var scopeProperties = new Dictionary<string, object>();

    if (!string.IsNullOrEmpty(parentInstanceId))
    {
        activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
        activity?.SetBaggage(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
        scopeProperties[TelemetryConstants.TagNames.ParentInstanceId] = parentInstanceId;
    }

    if (!string.IsNullOrEmpty(rootInstanceId))
    {
        activity?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId);
        activity?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId);
        scopeProperties[TelemetryConstants.TagNames.RootInstanceId] = rootInstanceId;
    }

    if (scopeProperties.Count == 0)
    {
        await next(context);
        return;
    }

    using (logger.BeginScope(scopeProperties))
    {
        await next(context);
    }
}
```

- [ ] **Step 5.2: Update the XML summary on the class**

Change the class-level `<summary>` to:
```csharp
/// <summary>
/// Middleware that reads <c>X-Parent-Instance-Id</c> and <c>X-Root-Instance-Id</c> request headers
/// (when present) and adds them to the current Activity (tag and baggage) and to the log scope,
/// so that traces and logs for subflow/subprocess requests are searchable by parent and root instance ID.
/// Should be registered after UseCorrelationId() and before controllers.
/// </summary>
```

- [ ] **Step 5.3: Build shared project**

```bash
dotnet build src/BBT.Workflow.HttpApi.Shared/BBT.Workflow.HttpApi.Shared.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5.4: Commit**

```bash
git add src/BBT.Workflow.HttpApi.Shared/Middlewares/ParentInstanceIdEnrichmentMiddleware.cs
git commit -m "feat(middleware): extend ParentInstanceIdEnrichmentMiddleware to stamp X-Root-Instance-Id"
```

---

## Task 6: Stamp root ID in `TransitionExecutor` pipeline scope

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs`

Every time the pipeline runs for a subflow instance (B, C, D), the root ID should appear in every log line and span tag for the duration of the execution.

- [ ] **Step 6.1: Add root ID to `BuildLogScope`**

Locate `BuildLogScope`. The current dictionary ends around `TriggerType`. Add after it:

```csharp
private static Dictionary<string, object> BuildLogScope(TransitionExecutionContext context)
{
    var props = new Dictionary<string, object>
    {
        [TelemetryConstants.TagNames.Domain]        = context.Domain,
        [TelemetryConstants.TagNames.Flow]          = context.Workflow.Key,
        [TelemetryConstants.TagNames.FlowVersion]   = context.Workflow.Version,
        [TelemetryConstants.TagNames.InstanceId]    = context.InstanceId,
        [TelemetryConstants.TagNames.InstanceKey]   = context.Instance.Key ?? "N/A",
        [TelemetryConstants.TagNames.StateFrom]     = context.Transition?.From ?? context.Instance.GetCurrentState,
        [TelemetryConstants.TagNames.StateTo]       = context.Transition?.Target ?? "N/A",
        [TelemetryConstants.TagNames.TransitionKey] = context.TransitionKey,
        [TelemetryConstants.TagNames.TriggerType]   = context.Transition?.TriggerType.ToString() ?? "N/A",
    };

    // Stamp root instance ID for every subflow pipeline execution (no-op on root instances)
    var rootId = context.Instance.GetRootInstanceId();
    if (rootId != context.InstanceId)
    {
        props[TelemetryConstants.TagNames.RootInstanceId] = rootId;
    }

    return props;
}
```

- [ ] **Step 6.2: Add root ID tag to `EnrichTelemetry`**

In `EnrichTelemetry`, after `activity.SetBaggage(TelemetryConstants.TagNames.InstanceId, context.InstanceId.ToString());`, add:

```csharp
var rootId = context.Instance.GetRootInstanceId();
if (rootId != context.InstanceId)
{
    activity.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
    activity.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootId.ToString());
}
```

- [ ] **Step 6.3: Build**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6.4: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs
git commit -m "feat(pipeline): stamp root instance ID in TransitionExecutor log scope and activity tags"
```

---

## Task 7: Add root ID to `StartSubflowJobHandler` log scope

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/PostCommit/Handlers/StartSubflowJobHandler.cs`

This handler runs post-commit when the parent instance (B) enqueues the start of C. The refreshed `instance` loaded inside is the parent, which has `root.instance.id` if B is itself a subflow.

- [ ] **Step 7.1: Extend the log scope**

Locate the `using (logger.BeginScope(new Dictionary<string, object> { ... }))` block (around line 28):

```csharp
// Current:
using (logger.BeginScope(new Dictionary<string, object>
{
    [TelemetryConstants.TagNames.InstanceId] = context.InstanceId
}))
```

Replace with:
```csharp
using (logger.BeginScope(new Dictionary<string, object>
{
    [TelemetryConstants.TagNames.InstanceId] = context.InstanceId,
    [TelemetryConstants.TagNames.RootInstanceId] = context.Instance.GetRootInstanceId()
}))
```

Note: `context.Instance` is the parent instance already loaded in the `TransitionExecutionContext`. It has `ExtraProperties` populated.

- [ ] **Step 7.2: Build**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 7.3: Run full test suite**

```bash
dotnet test --no-restore -q
```

Expected: all existing tests pass. Any new failures indicate a regression to investigate.

- [ ] **Step 7.4: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/PostCommit/Handlers/StartSubflowJobHandler.cs
git commit -m "feat(subflow): add root instance ID to StartSubflowJobHandler log scope"
```

---

## Task 8: Integration smoke-test checklist

No automated integration tests for this feature — it is observability-only. Verify manually with a 3-level chain (A→B→C) in Docker dev mode.

- [ ] **Step 8.1: Start infrastructure**

```bash
cd etc/docker && ./run-docker.sh dev
```

- [ ] **Step 8.2: Start an A-flow instance and note its ID (call it `A_ID`)**

```bash
curl -s -X POST http://localhost:4201/api/workflow/{flow-key}/instances \
  -H "Content-Type: application/json" -d '{"key":"smoke-test"}' | jq '.id'
```

- [ ] **Step 8.3: Drive A until it starts subflow B, then B until it starts subflow C**

Trigger the transitions that enter the subflow states via the transition endpoint.

- [ ] **Step 8.4: Verify root ID in ExtraProperties for B and C**

Query instances by their IDs through the API and confirm `ExtraProperties["root.instance.id"]` equals `A_ID` on both B and C.

- [ ] **Step 8.5: Verify Jaeger traces**

Open Jaeger at `http://localhost:16686`. Search by tag `vnext.root.instance.id = <A_ID>`. All spans from A, B, and C should appear in one query.

- [ ] **Step 8.6: Verify structured logs**

In the container log output, filter by `A_ID`. Log lines from the B and C pipeline executions should include `vnext.root.instance.id` in the structured payload.

---

## Self-Review

**Spec coverage:**
- ✅ Root ID persisted in `ExtraProperties` (Task 3.4) — searchable on Instance
- ✅ Root ID forwarded via HTTP header (Task 3.6) — cross-domain propagation
- ✅ Root ID in OTel Activity tags (Tasks 4, 6.2) — Jaeger/tracing searchable
- ✅ Root ID in log scope (Tasks 3.3, 5, 6.1, 7.1) — structured log searchable
- ✅ Deep chain: A→B→C→D all carry A's ID via `GetRootInstanceId()` fallback (Task 2.1)
- ✅ A-flow itself is unaffected: `GetRootInstanceId()` returns self when key is absent

**Placeholder scan:** None found. All code blocks are complete and runnable.

**Type consistency:**
- `GetRootInstanceId()` returns `Guid` everywhere
- `TelemetryConstants.TagNames.RootInstanceId` is the single string constant used in all places
- `DomainConsts.MetaDataKeys.RootInstanceId` is the single storage key used in both write (SubflowStarter) and read (GetRootInstanceId, TransitionExecutor)
