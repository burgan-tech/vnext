# ScriptMapping Related Instance Data Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give mapping scripts a `context.Related` API that reads a related instance's data one hop up (parent) or one hop down (own correlations), without duplicating data across the parent/child boundary.

**Architecture:** A lazily-resolving accessor hangs off `ScriptContext` (Domain). It derives the parent reference from `Instance.ExtraProperties` (`parent.*`) and child references from `IInstanceCorrelationRepository.GetByParentAsync`. The actual read goes through `IRelatedInstanceReader`, implemented by a routed gateway that reads locally when `IRuntimeInfoProvider.IsDomainMatch` and calls a new internal HTTP endpoint otherwise. Reads are system-identity and unfiltered (no query-role check, no `x-roles` filtering, no extensions), memoized per `ScriptContext`, and capped per context.

**Tech Stack:** .NET 10, C# 13, Aether SDK (`Result<T>`, repositories, multi-schema, `LoggerMessage` source generators), EF Core, Dapr service invocation, xUnit + Moq + NSubstitute + Shouldly.

**Spec:** `docs/superpowers/specs/2026-07-31-scriptmapping-related-instance-access-design.md`

---

## Deviations from the spec (deliberate, decided while mapping to real code)

1. **`LocalRelatedInstanceReader` moves from Application to `Infrastructure/Gateway`.** The existing
   `LocalInstanceQueryGateway` pattern uses `IServiceScopeFactory.ExecuteWithWorkflowAsync(domain, workflow, version, ...)`
   to establish schema scope, and lives in Infrastructure. The Application layer gets
   `IRelatedInstanceQueryAppService` (the actual repository read) instead, matching
   `IInstanceQueryAppService`. Net effect: one extra small interface, exact pattern parity.
2. **`IScriptContextBuilder` is NOT modified.** `ScriptContextBuilder` constructs the accessor itself
   from its own injected dependencies, so no fluent `WithRelated` is needed on the outer builder. Only
   the inner `ScriptContext.Builder` gets `SetRelated`.
3. **`RoutedRelatedInstanceReader` injects both sides as interfaces via keyed DI**, unlike
   `RoutedInstanceQueryGateway`, which injects concrete `Local*`/`Remote*` classes and is therefore
   untestable. The routing decision is the whole value of this class, so it has to be unit-testable.
4. **`ExtraProperties` value parsing must be defensive.** `SubflowStarter` writes
   `ExtraProperties["parent.id"] = parentInstance.Id` as a `Guid`, but after a JSON round-trip through
   the database the same slot can hold a `string` or a `JsonElement`. Task 3 handles all three.

---

## File Structure

**New — Domain (`src/BBT.Workflow.Domain/Scripting/Related/`)**

| File | Responsibility |
|---|---|
| `RelatedInstanceRef.cs` | Address of a related instance (id + domain + flow + version) |
| `RelatedInstanceSnapshot.cs` | What the reader returns: identity, status, raw data |
| `RelatedInstanceView.cs` | What scripts see: snapshot + correlation facts |
| `RelatedAccessOptions.cs` | `MaxResolutionsPerContext` |
| `RelatedInstanceAccessException.cs` | Thrown on read failure / limit exceeded |
| `IRelatedInstanceReader.cs` | Reader contract (implemented in Infrastructure) |
| `IRelatedInstanceAccessor.cs` | Script-facing contract |
| `NullRelatedInstanceAccessor.cs` | No-op fallback when no reader is wired |
| `RelatedInstanceAccessor.cs` | Reference resolution, memo, limit, Result→exception mapping |

**New — Application (`src/BBT.Workflow.Application/Instances/Related/`)**

| File | Responsibility |
|---|---|
| `IRelatedInstanceQueryAppService.cs` | Local, unfiltered instance read contract |
| `RelatedInstanceQueryAppService.cs` | Repository read + snapshot projection |

**New — Infrastructure (`src/BBT.Workflow.Infrastructure/Gateway/`)**

| File | Responsibility |
|---|---|
| `LocalRelatedInstanceReader.cs` | Schema-scoped local dispatch |
| `RemoteRelatedInstanceReader.cs` | Internal HTTP endpoint call |
| `RoutedRelatedInstanceReader.cs` | Domain-match routing |

**Modified**

| File | Change |
|---|---|
| `src/BBT.Workflow.Domain/WorkflowErrorCodes.cs` | `RelatedInstanceReadFailed` (`Instance:100033`) |
| `src/BBT.Workflow.Domain/Instances/IInstanceRepository.cs` | `FindByIdsAsReadOnlyAsync` (batched, data-only) |
| `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs` | implementation of the batched read |
| `src/BBT.Workflow.Domain/Scripting/Models.cs` | `ScriptContext.Related`, `Builder.SetRelated`, `Dispose`, `CreateParallelBranch` |
| `src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs` | Build the accessor |
| `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` | 7 new `[LoggerMessage]` partials (20430–20436) |
| `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs` | 2 internal route templates |
| `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/GatewayServiceCollectionExtensions.cs` | Register the three readers |
| `orchestration/.../Controllers/Instances/InstanceController.cs` | 2 internal endpoints |
| `docs/runtime/script-related-instance-access.md` (new), `docs/README.md`, `.claude/rules/vnext-workflow-developer.md`, `vnext-meta/features.json` | Documentation |

**Test files**

| File | Covers |
|---|---|
| `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorParentTests.cs` | Task 3 |
| `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorSubTests.cs` | Task 4 |
| `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorMemoAndLimitTests.cs` | Task 5 |
| `test/BBT.Workflow.Domain.Tests/Scripting/Related/NullRelatedInstanceAccessorTests.cs` | Task 1 |
| `test/BBT.Workflow.Domain.Tests/Scripting/Related/ScriptContextRelatedTests.cs` | Task 6 |
| `test/BBT.Workflow.Application.Tests/Instances/Related/RelatedInstanceQueryAppServiceTests.cs` | Task 8 |
| `test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedRelatedInstanceReaderTests.cs` | Task 11 |

---

## Baseline warning (read before you start)

`master` has a large number of pre-existing test failures (mostly `AmbientServiceProvider` leakage
across parallel collections). Do not treat those as regressions. The controller captured the baseline
into `/tmp/test-baseline.txt` before Task 1 — do not overwrite it.

**The repository root holds two solution files (`vnext.sln` and `BBT.Workflow.slnx`), so a bare
`dotnet build` or `dotnet test` fails with `MSB1011`. Always pass an explicit target:**

```bash
dotnet build vnext.sln
```

Always run the *specific* tests you wrote with `--filter` against the *specific* test project, never the
whole suite, to judge your work:

```bash
dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~YourTestClass"
```

First-time setup on macOS/Linux (required for PostSharp on .NET 10):

```bash
./scripts/setup-netstandard-ref.sh
```

---

## Task 1: Domain value types and contracts

**Files:**
- Create: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceRef.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceSnapshot.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceView.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/RelatedAccessOptions.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessException.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/IRelatedInstanceReader.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/IRelatedInstanceAccessor.cs`
- Create: `src/BBT.Workflow.Domain/Scripting/Related/NullRelatedInstanceAccessor.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/Related/NullRelatedInstanceAccessorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Scripting/Related/NullRelatedInstanceAccessorTests.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class NullRelatedInstanceAccessorTests
{
    [Fact]
    public void HasParent_ShouldBeFalse()
    {
        NullRelatedInstanceAccessor.Instance.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull()
    {
        var result = await NullRelatedInstanceAccessor.Instance.ParentAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SubAsync_ShouldReturnNull()
    {
        var result = await NullRelatedInstanceAccessor.Instance.SubAsync("any-flow", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SubsAsync_ShouldReturnEmptyList()
    {
        var result = await NullRelatedInstanceAccessor.Instance.SubsAsync(null, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SubKeysAsync_ShouldReturnEmptyList()
    {
        var result = await NullRelatedInstanceAccessor.Instance.SubKeysAsync(CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~NullRelatedInstanceAccessorTests"`

Expected: build FAILS — `CS0246: The type or namespace name 'NullRelatedInstanceAccessor' could not be found`.

- [ ] **Step 3: Create `RelatedInstanceRef.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Address of a related workflow instance. Carries everything the reader needs to route the read
/// (domain for local-vs-remote dispatch, flow for schema resolution) without a lookup.
/// </summary>
/// <param name="InstanceId">The related instance identifier.</param>
/// <param name="Domain">The domain that owns the related instance.</param>
/// <param name="Flow">The workflow key of the related instance.</param>
/// <param name="FlowVersion">The workflow version, when known.</param>
public sealed record RelatedInstanceRef(
    Guid InstanceId,
    string Domain,
    string Flow,
    string? FlowVersion);
```

- [ ] **Step 4: Create `RelatedInstanceSnapshot.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Raw read result for a related instance, as produced by <see cref="IRelatedInstanceReader"/>.
/// Contains only facts owned by the target instance — correlation facts are added later by the
/// accessor, which owns the relationship record.
/// </summary>
public sealed class RelatedInstanceSnapshot
{
    /// <summary>The related instance identifier.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>Business key of the related instance, when it has one.</summary>
    public string? Key { get; init; }

    /// <summary>Domain that owns the related instance.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Workflow key of the related instance.</summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>Workflow version of the related instance.</summary>
    public string? FlowVersion { get; init; }

    /// <summary>Instance status code: A, B, C, F or P.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Current state key of the related instance.</summary>
    public string? CurrentState { get; init; }

    /// <summary>True when the related instance itself reached a completed terminal status.</summary>
    public bool IsCompleted { get; init; }

    /// <summary>Latest instance data, unfiltered by x-roles and without extensions.</summary>
    public dynamic? Data { get; init; }
}
```

- [ ] **Step 5: Create `RelatedInstanceView.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Script-facing projection of a related instance. Combines the read snapshot with the correlation
/// facts the current instance owns.
/// </summary>
/// <remarks>
/// <see cref="IsCompleted"/> (target instance status) and <see cref="CorrelationCompleted"/>
/// (relationship closed) are separate on purpose: a subflow instance can be Completed while the
/// parent correlation is still open — the subflow completion window. Conflating them produces wrong
/// decisions.
/// </remarks>
public sealed class RelatedInstanceView
{
    /// <summary>The related instance identifier.</summary>
    public Guid InstanceId { get; init; }

    /// <summary>Business key of the related instance, when it has one.</summary>
    public string? Key { get; init; }

    /// <summary>Domain that owns the related instance.</summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>Workflow key of the related instance.</summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>Workflow version of the related instance.</summary>
    public string? FlowVersion { get; init; }

    /// <summary>Instance status code: A, B, C, F or P.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Current state key of the related instance.</summary>
    public string? CurrentState { get; init; }

    /// <summary>True when the related instance itself reached a completed terminal status.</summary>
    public bool IsCompleted { get; init; }

    /// <summary>
    /// Whether the correlation linking this instance to the current one is closed.
    /// Null for the parent direction, where no correlation is involved.
    /// </summary>
    public bool? CorrelationCompleted { get; init; }

    /// <summary>
    /// Correlation terminal outcome name (Completed / Faulted / Canceled).
    /// Null for the parent direction, and null while the correlation is open.
    /// </summary>
    public string? TerminalOutcome { get; init; }

    /// <summary>
    /// "S" (SubFlow) or "P" (SubProcess) for the down direction. Null for the parent.
    /// </summary>
    public string? SubFlowType { get; init; }

    /// <summary>Latest instance data, unfiltered by x-roles and without extensions.</summary>
    public dynamic? Data { get; init; }
}
```

- [ ] **Step 6: Create `RelatedAccessOptions.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Guardrails for related-instance access from mapping scripts.
/// Bound from the <c>Workflow:Scripting:RelatedAccess</c> configuration section.
/// </summary>
public sealed class RelatedAccessOptions
{
    /// <summary>Configuration section name this options class binds from.</summary>
    public const string SectionName = "Workflow:Scripting:RelatedAccess";

    /// <summary>
    /// Maximum number of distinct related instances a single ScriptContext may resolve.
    /// Memoized repeat reads do not count. Exceeding the cap throws
    /// <see cref="RelatedInstanceAccessException"/> — a script needing more than this is a design error.
    /// </summary>
    public int MaxResolutionsPerContext { get; set; } = 10;
}
```

- [ ] **Step 7: Create `RelatedInstanceAccessException.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Thrown when a related-instance read fails or the per-context resolution cap is exceeded.
/// Absence (no parent, no correlation, instance gone) is reported as null instead — a read failure
/// must never be mistaken for absence, because that silently produces a wrong business decision.
/// </summary>
public sealed class RelatedInstanceAccessException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public RelatedInstanceAccessException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public RelatedInstanceAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 8: Create `IRelatedInstanceReader.cs`**

```csharp
using BBT.Aether.Results;

namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Reads related instances with the engine's own system identity: no query-role check, no x-roles
/// field filtering, no extensions, no data-function response cache. Implemented by a routed gateway
/// that reads locally when the target domain matches the runtime and calls an internal endpoint
/// otherwise.
/// </summary>
/// <remarks>
/// A successful <see cref="Result{T}"/> carrying null means "not found" and is normal. A failed Result
/// means an infrastructure problem and is converted into
/// <see cref="RelatedInstanceAccessException"/> by the accessor.
/// </remarks>
public interface IRelatedInstanceReader
{
    /// <summary>Reads a single related instance.</summary>
    Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads several related instances, grouping by domain so each domain is contacted once.
    /// References that resolve to nothing are omitted from the result rather than reported as errors.
    /// </summary>
    Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 9: Create `IRelatedInstanceAccessor.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Script-facing access to instances related to the current one — one hop up (the parent that started
/// this instance as a SubFlow/SubProcess) or one hop down (this instance's own correlations).
/// Exposed as <c>ScriptContext.Related</c>.
/// </summary>
/// <remarks>
/// Nothing is pre-fetched: the first call that needs data performs the read, and results are memoized
/// for the lifetime of the owning ScriptContext. Reads are unfiltered — copying a related instance's
/// field into the current instance's data makes it reachable by any client entitled to read the
/// current instance, because x-roles protection does not follow the copy.
/// </remarks>
public interface IRelatedInstanceAccessor
{
    /// <summary>
    /// True when this instance was started by a parent as a SubFlow or SubProcess.
    /// Reads instance metadata only — never performs a data read.
    /// </summary>
    bool HasParent { get; }

    /// <summary>
    /// Sub workflow keys of this instance's correlations, in correlation creation order, duplicates
    /// removed. Loads the correlation list (once) but reads no instance data.
    /// </summary>
    Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The parent instance, or null when this instance has no parent.
    /// </summary>
    /// <exception cref="RelatedInstanceAccessException">The read failed, or the cap was exceeded.</exception>
    Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recently created correlation whose sub workflow key matches, or null when there is none.
    /// </summary>
    /// <param name="subFlowKey">Sub workflow key, matched against <c>InstanceCorrelation.SubFlowName</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RelatedInstanceAccessException">The read failed, or the cap was exceeded.</exception>
    Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// All correlations ordered by creation time, optionally filtered by sub workflow key.
    /// Active and completed correlations are both included. Reads are batched — never N+1.
    /// </summary>
    /// <param name="subFlowKey">Sub workflow key filter, or null for every correlation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RelatedInstanceAccessException">The read failed, or the cap was exceeded.</exception>
    Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 10: Create `NullRelatedInstanceAccessor.cs`**

```csharp
namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// No-op accessor used when a ScriptContext is built without a reader — unit tests and any code path
/// that constructs <c>ScriptContext.Builder</c> directly. Reports no parent and no correlations so
/// scripts and existing tests behave as if the instance were standalone.
/// </summary>
public sealed class NullRelatedInstanceAccessor : IRelatedInstanceAccessor
{
    /// <summary>The shared stateless instance.</summary>
    public static readonly NullRelatedInstanceAccessor Instance = new();

    private NullRelatedInstanceAccessor()
    {
    }

    /// <inheritdoc />
    public bool HasParent => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<RelatedInstanceView?>(null);

    /// <inheritdoc />
    public Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<RelatedInstanceView?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RelatedInstanceView>>([]);
}
```

- [ ] **Step 11: Run test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~NullRelatedInstanceAccessorTests"`

Expected: PASS, 5 tests.

- [ ] **Step 12: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Related test/BBT.Workflow.Domain.Tests/Scripting/Related
git commit -m "feat(scripting): add related instance access contracts and value types"
```

---

## Task 2: Logging extensions

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` (append at end of the 20xxx block; the last used id is 20425)

No test — `LoggerMessage` partials are compile-time generated and verified by the tasks that call them.

- [ ] **Step 1: Append the seven partials**

Add to `WorkflowLogs.cs`, after the existing `EventId = 20425` method:

```csharp
    [LoggerMessage(
        EventId = 20430,
        Level = LogLevel.Debug,
        Message = "Related instance resolved. Instance: {InstanceId}, Direction: {Direction}, Target: {TargetInstanceId}, Domain: {TargetDomain}, Flow: {TargetFlow}")]
    public static partial void RelatedInstanceResolved(
        this ILogger logger,
        Guid instanceId,
        string direction,
        Guid targetInstanceId,
        string targetDomain,
        string targetFlow);

    [LoggerMessage(
        EventId = 20431,
        Level = LogLevel.Debug,
        Message = "Related instance not found. Instance: {InstanceId}, Direction: {Direction}, Key: {Key}")]
    public static partial void RelatedInstanceNotFound(
        this ILogger logger,
        Guid instanceId,
        string direction,
        string? key);

    [LoggerMessage(
        EventId = 20432,
        Level = LogLevel.Debug,
        Message = "Related instance cross-domain read. Instance: {InstanceId}, TargetDomain: {TargetDomain}, TargetFlow: {TargetFlow}, Count: {Count}")]
    public static partial void RelatedInstanceCrossDomainRead(
        this ILogger logger,
        Guid instanceId,
        string targetDomain,
        string targetFlow,
        int count);

    [LoggerMessage(
        EventId = 20433,
        Level = LogLevel.Error,
        Message = "Related instance resolution failed. Instance: {InstanceId}, Direction: {Direction}, Target: {TargetInstanceId}, TargetDomain: {TargetDomain}, TargetFlow: {TargetFlow}, Reason: {Reason}")]
    public static partial void RelatedInstanceResolutionFailed(
        this ILogger logger,
        Guid instanceId,
        string direction,
        Guid targetInstanceId,
        string targetDomain,
        string targetFlow,
        string reason);

    [LoggerMessage(
        EventId = 20434,
        Level = LogLevel.Warning,
        Message = "Related instance resolution limit exceeded. Instance: {InstanceId}, Limit: {Limit}")]
    public static partial void RelatedInstanceResolutionLimitExceeded(
        this ILogger logger,
        Guid instanceId,
        int limit);

    [LoggerMessage(
        EventId = 20435,
        Level = LogLevel.Error,
        Message = "Related instance read failed. Target: {TargetInstanceId}, Flow: {TargetFlow}")]
    public static partial void RelatedInstanceReadFailed(
        this ILogger logger,
        Exception exception,
        Guid targetInstanceId,
        string targetFlow);

    [LoggerMessage(
        EventId = 20436,
        Level = LogLevel.Error,
        Message = "Related instance batch resolution failed. Instance: {InstanceId}, Count: {Count}, TargetDomains: {TargetDomains}, Reason: {Reason}")]
    public static partial void RelatedInstanceBatchResolutionFailed(
        this ILogger logger,
        Guid instanceId,
        int count,
        string targetDomains,
        string reason);
```

Rationale for the two deviations from the neighbouring 20xxx block, both established by reviewing this
file's own conventions:

- **20433 is `Error`, not `Warning`.** The accessor logs it and then *throws*
  `RelatedInstanceAccessException`. Every fails-and-propagates event in this file is `Error`
  (`TaskExecutionFailed` 10071, `TaskInvocationFailed` 10076, `TransitionContinuationEnqueueFailed`
  10123). The `Warning` cases (`InstanceSchemaFunctionCacheError` 20423, `ResourceLockAutoReleaseError`
  10105) are all swallowed-and-degrade, which this is not. 20434 stays `Warning` because it matches
  `MaxAutoHopsExceeded` (10045) — a cap was hit and the flow aborts, which this file treats as Warning.
- **20433 carries `TargetDomain`/`TargetFlow`.** Failures here are disproportionately cross-domain HTTP
  failures. Without them the failure log is *less* diagnosable than the success log for the same
  operation (20430 already carries both), which is backwards. Every call site has the
  `RelatedInstanceRef` in hand, so there is no cost to supplying them.
- **20435 exists** so the Application-layer reader in Task 8 has a `WorkflowLogs` method that takes the
  caught `Exception`, instead of the raw `logger.LogError(...)` the coding standard forbids. It is
  separate from 20433 because the two fire at different layers: 20435 at the repository read (has a real
  exception), 20433 at the accessor (has a failed `Result`, no exception).

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/BBT.Workflow.Domain`

Expected: `Build succeeded`. If you see `SYSLIB1006` (duplicate event id), another branch took 20430–20436 — pick the next free block and keep the seven ids contiguous.

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs
git commit -m "feat(logging): add related instance access log messages (20430-20436)"
```

---

## Task 3: Accessor — parent resolution

**Files:**
- Create: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs`
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorParentTests.cs`

Background you need: `SubflowStarter` writes the parent address into the subflow instance's
`ExtraProperties` using `DomainConsts.MetaDataKeys` — `parent.id`, `parent.key`, `parent.domain`,
`parent.flow`, `parent.version`. `parent.id` is written as a `Guid`, but a database round-trip can turn
it into a `string` or a `System.Text.Json.JsonElement`, so parsing must accept all three.

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorParentTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Data;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class RelatedInstanceAccessorParentTests
{
    private static readonly Guid ChildId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Instance ChildWithParentMetadata(object parentIdValue)
    {
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0", "customer-42");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = parentIdValue,
            [DomainConsts.MetaDataKeys.Key] = "customer-42",
            [DomainConsts.MetaDataKeys.Domain] = "lending",
            [DomainConsts.MetaDataKeys.Flow] = "loan-application",
            [DomainConsts.MetaDataKeys.Version] = "2.1.0"
        });
        return instance;
    }

    private static RelatedInstanceSnapshot ParentSnapshot() => new()
    {
        InstanceId = ParentId,
        Key = "customer-42",
        Domain = "lending",
        Flow = "loan-application",
        FlowVersion = "2.1.0",
        Status = "A",
        CurrentState = "awaiting-kyc",
        IsCompleted = false,
        Data = null
    };

    private static RelatedInstanceAccessor CreateAccessor(
        Instance instance,
        IRelatedInstanceReader reader,
        IInstanceCorrelationRepository? correlationRepository = null) =>
        new(
            instance,
            reader,
            correlationRepository ?? Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(),
            NullLogger.Instance);

    [Fact]
    public void HasParent_ShouldBeFalse_WhenNoParentMetadata()
    {
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull_WhenNoParentMetadata()
    {
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        var reader = Substitute.For<IRelatedInstanceReader>();
        var accessor = CreateAccessor(instance, reader);

        var parent = await accessor.ParentAsync(CancellationToken.None);

        parent.ShouldBeNull();
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull_WhenParentIdIsUnparsable()
    {
        var instance = ChildWithParentMetadata("not-a-guid");
        var reader = Substitute.For<IRelatedInstanceReader>();
        var accessor = CreateAccessor(instance, reader);

        accessor.HasParent.ShouldBeFalse();
        (await accessor.ParentAsync(CancellationToken.None)).ShouldBeNull();
    }

    public static TheoryData<object> ParentIdRepresentations() =>
        new()
        {
            ParentId,
            ParentId.ToString(),
            JsonSerializer.SerializeToElement(ParentId)
        };

    [Theory]
    [MemberData(nameof(ParentIdRepresentations))]
    public async Task ParentAsync_ShouldResolveParent_ForEveryStoredIdRepresentation(object parentIdValue)
    {
        var instance = ChildWithParentMetadata(parentIdValue);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(ParentSnapshot()));
        var accessor = CreateAccessor(instance, reader);

        accessor.HasParent.ShouldBeTrue();
        var parent = await accessor.ParentAsync(CancellationToken.None);

        parent.ShouldNotBeNull();
        parent!.InstanceId.ShouldBe(ParentId);
        parent.Domain.ShouldBe("lending");
        parent.Flow.ShouldBe("loan-application");
        parent.Status.ShouldBe("A");
        parent.CurrentState.ShouldBe("awaiting-kyc");
    }

    [Fact]
    public async Task ParentAsync_ShouldLeaveCorrelationFieldsNull()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(ParentSnapshot()));
        var accessor = CreateAccessor(instance, reader);

        var parent = await accessor.ParentAsync(CancellationToken.None);

        parent!.CorrelationCompleted.ShouldBeNull();
        parent.TerminalOutcome.ShouldBeNull();
        parent.SubFlowType.ShouldBeNull();
    }

    [Fact]
    public async Task ParentAsync_ShouldPassTheRefFromMetadata()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(ParentSnapshot()));
        var accessor = CreateAccessor(instance, reader);

        await accessor.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(
            Arg.Is<RelatedInstanceRef>(r =>
                r.InstanceId == ParentId &&
                r.Domain == "lending" &&
                r.Flow == "loan-application" &&
                r.FlowVersion == "2.1.0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentIdIsEmptyGuid()
    {
        var instance = ChildWithParentMetadata(Guid.Empty);
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentIdIsNonStringJson()
    {
        // Exercises the `when element.ValueKind == JsonValueKind.String` guard in ReadGuid.
        // Without the guard, TryGetGuid throws InvalidOperationException on a non-string element.
        var instance = ChildWithParentMetadata(JsonSerializer.SerializeToElement(42));
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentFlowIsMissing()
    {
        // parent.id and parent.domain resolve, parent.flow does not — forces the
        // `IsNullOrWhiteSpace(domain) || IsNullOrWhiteSpace(flow)` branch in BuildParentRef.
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = "lending"
        });
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentDomainHasUnexpectedStoredType()
    {
        // ReadString must fail closed rather than fabricating a ToString() value, which would
        // otherwise pass BuildParentRef's non-empty check and point at a domain that never existed.
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = new object(),
            [DomainConsts.MetaDataKeys.Flow] = "loan-application"
        });
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull_WhenReaderReportsNotFound()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        var accessor = CreateAccessor(instance, reader);

        (await accessor.ParentAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ParentAsync_ShouldThrow_WhenReaderFails()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Fail(
                Error.Failure("RELATED_READ", "endpoint unreachable")));
        var accessor = CreateAccessor(instance, reader);

        var exception = await Should.ThrowAsync<RelatedInstanceAccessException>(
            () => accessor.ParentAsync(CancellationToken.None));

        exception.Message.ShouldContain("endpoint unreachable");
    }
}
```

If `ExtraPropertyDictionary` does not resolve from `BBT.Aether.Data`, find its namespace with:

```bash
grep -rn "class ExtraPropertyDictionary" ~/.nuget/packages/bbt.aether* --include=*.cs 2>/dev/null | head -3
```

and fall back to copying the `using` block from `src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs`, which constructs one.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~RelatedInstanceAccessorParentTests"`

Expected: build FAILS — `CS0246: ... 'RelatedInstanceAccessor' could not be found`.

- [ ] **Step 3: Create `RelatedInstanceAccessor.cs` with parent support only**

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Default <see cref="IRelatedInstanceAccessor"/>. Derives the parent reference from the instance's
/// <c>parent.*</c> metadata and child references from the correlation repository, delegates reads to
/// <see cref="IRelatedInstanceReader"/>, memoizes results for the lifetime of the owning ScriptContext,
/// and caps how many distinct related instances one context may resolve.
/// </summary>
public sealed class RelatedInstanceAccessor : IRelatedInstanceAccessor
{
    private const string DirectionParent = "parent";

    private readonly Instance _instance;
    private readonly IRelatedInstanceReader _reader;
    private readonly IInstanceCorrelationRepository _correlationRepository;
    private readonly RelatedAccessOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, RelatedInstanceView> _memo;
    private readonly RelatedInstanceRef? _parentRef;

    /// <summary>Creates an accessor bound to one instance snapshot.</summary>
    public RelatedInstanceAccessor(
        Instance instance,
        IRelatedInstanceReader reader,
        IInstanceCorrelationRepository correlationRepository,
        RelatedAccessOptions options,
        ILogger logger)
        : this(instance, reader, correlationRepository, options, logger, new ConcurrentDictionary<Guid, RelatedInstanceView>())
    {
    }

    private RelatedInstanceAccessor(
        Instance instance,
        IRelatedInstanceReader reader,
        IInstanceCorrelationRepository correlationRepository,
        RelatedAccessOptions options,
        ILogger logger,
        ConcurrentDictionary<Guid, RelatedInstanceView> memo)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _correlationRepository = correlationRepository ?? throw new ArgumentNullException(nameof(correlationRepository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memo = memo;
        _parentRef = BuildParentRef(instance);
    }

    /// <inheritdoc />
    public bool HasParent => _parentRef != null;

    /// <inheritdoc />
    public async Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default)
    {
        if (_parentRef == null)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, DirectionParent, null);
            return null;
        }

        return await ResolveAsync(_parentRef, DirectionParent, correlation: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    /// <inheritdoc />
    public Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    /// <inheritdoc />
    public Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    private async Task<RelatedInstanceView?> ResolveAsync(
        RelatedInstanceRef reference,
        string direction,
        InstanceCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        if (_memo.TryGetValue(reference.InstanceId, out var cached))
            return cached;

        EnsureUnderLimit();

        var result = await _reader.ReadAsync(reference, cancellationToken);
        if (!result.IsSuccess)
        {
            var reason = result.Error.Message ?? "unknown";
            _logger.RelatedInstanceResolutionFailed(
                _instance.Id, direction, reference.InstanceId, reference.Domain, reference.Flow, reason);
            throw new RelatedInstanceAccessException(
                $"Failed to read related instance {reference.InstanceId} ({direction}): {reason}");
        }

        var snapshot = result.Value;
        if (snapshot == null)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, direction, reference.Flow);
            return null;
        }

        var view = ToView(snapshot, correlation);
        _memo[reference.InstanceId] = view;
        _logger.RelatedInstanceResolved(
            _instance.Id, direction, snapshot.InstanceId, snapshot.Domain, snapshot.Flow);
        return view;
    }

    private void EnsureUnderLimit()
    {
        if (_memo.Count < _options.MaxResolutionsPerContext)
            return;

        _logger.RelatedInstanceResolutionLimitExceeded(_instance.Id, _options.MaxResolutionsPerContext);
        throw new RelatedInstanceAccessException(
            $"Related instance resolution limit of {_options.MaxResolutionsPerContext} exceeded for instance {_instance.Id}. " +
            "Reduce the number of distinct related instances a single script resolves, or raise " +
            $"{RelatedAccessOptions.SectionName}:MaxResolutionsPerContext.");
    }

    private static RelatedInstanceView ToView(RelatedInstanceSnapshot snapshot, InstanceCorrelation? correlation) =>
        new()
        {
            InstanceId = snapshot.InstanceId,
            Key = snapshot.Key,
            Domain = snapshot.Domain,
            Flow = snapshot.Flow,
            FlowVersion = snapshot.FlowVersion,
            Status = snapshot.Status,
            CurrentState = snapshot.CurrentState,
            IsCompleted = snapshot.IsCompleted,
            CorrelationCompleted = correlation?.IsCompleted,
            TerminalOutcome = correlation?.TerminalOutcome?.ToString(),
            SubFlowType = correlation?.SubFlowType.Code,
            Data = snapshot.Data
        };

    private static RelatedInstanceRef? BuildParentRef(Instance instance)
    {
        var id = ReadGuid(instance, DomainConsts.MetaDataKeys.Id);
        if (id == null)
            return null;

        var domain = ReadString(instance, DomainConsts.MetaDataKeys.Domain);
        var flow = ReadString(instance, DomainConsts.MetaDataKeys.Flow);
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(flow))
            return null;

        return new RelatedInstanceRef(
            id.Value,
            domain!,
            flow!,
            ReadString(instance, DomainConsts.MetaDataKeys.Version));
    }

    /// <summary>
    /// Reads a Guid from instance metadata. The same slot can hold a Guid (freshly written by
    /// SubflowStarter), a string, or a JsonElement (after a database round-trip).
    /// </summary>
    private static Guid? ReadGuid(Instance instance, string key)
    {
        if (!instance.ExtraProperties.TryGetValue(key, out var raw) || raw == null)
            return null;

        return raw switch
        {
            Guid guid => guid == Guid.Empty ? null : guid,
            string text => Guid.TryParse(text, out var parsed) ? parsed : null,
            JsonElement element when element.ValueKind == JsonValueKind.String =>
                element.TryGetGuid(out var fromJson) ? fromJson : null,
            _ => Guid.TryParse(raw.ToString(), out var fallback) ? fallback : null
        };
    }

    /// <summary>
    /// Reads a string from instance metadata. Fails closed like <see cref="ReadGuid"/>: an unexpected
    /// stored type yields null rather than a fabricated <c>ToString()</c> value, which would otherwise
    /// pass the non-empty check in <see cref="BuildParentRef"/> and produce a reference to a domain or
    /// flow that never existed.
    /// </summary>
    private static string? ReadString(Instance instance, string key)
    {
        if (!instance.ExtraProperties.TryGetValue(key, out var raw) || raw == null)
            return null;

        return raw switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => null
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~RelatedInstanceAccessorParentTests"`

Expected: PASS, 14 tests (3 theory cases + 11 facts).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorParentTests.cs
git commit -m "feat(scripting): resolve parent instance data from parent.* metadata"
```

---

## Task 4: Accessor — correlation (down) resolution

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs` (replace the three `NotImplementedException` members)
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorSubTests.cs`

Background you need: `Instance.ChildCorrelations` is **not** usable here. `EfCoreInstanceRepository.WithDetailsAsync()`
includes `ChildCorrelations.Where(c => !c.IsCompleted)`, so completed correlations are missing from the
snapshot. `IInstanceCorrelationRepository.GetByParentAsync(parentInstanceId)` returns active **and**
completed correlations, which is what this feature needs. Widening the default include is not an option —
the repository include-strategy rule forbids it.

`InstanceCorrelation` derives from `AuditedEntity<Guid>` so it has `CreatedAt`. Fields used here:
`SubFlowInstanceId`, `SubFlowDomain`, `SubFlowName`, `SubFlowVersion`, `SubFlowType` (a `SubFlowType`
value object with a `.Code` of `"S"`/`"P"`), `IsCompleted`, `TerminalOutcome` (a nullable
`SubItemTerminalOutcome` enum: `Completed`/`Faulted`/`Canceled`).

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorSubTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class RelatedInstanceAccessorSubTests
{
    private static readonly Guid ParentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static InstanceCorrelation Correlation(
        Guid subInstanceId,
        string subFlowName,
        DateTime createdAt,
        string subFlowType = "S",
        string subFlowDomain = "compliance",
        bool completed = false,
        SubItemTerminalOutcome outcome = SubItemTerminalOutcome.Completed)
    {
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            ParentId,
            "awaiting-sub",
            subInstanceId,
            subFlowType,
            subFlowDomain,
            subFlowName,
            "1.0.0");
        correlation.CreatedAt = createdAt;
        if (completed)
            correlation.ApplyTerminalOutcome(outcome, createdAt.AddMinutes(1));
        return correlation;
    }

    private static RelatedInstanceSnapshot Snapshot(Guid id, string flow, string status = "C", bool isCompleted = true) => new()
    {
        InstanceId = id,
        Domain = "compliance",
        Flow = flow,
        FlowVersion = "1.0.0",
        Status = status,
        CurrentState = "done",
        IsCompleted = isCompleted
    };

    private static (RelatedInstanceAccessor Accessor, IRelatedInstanceReader Reader) CreateAccessor(
        params InstanceCorrelation[] correlations)
    {
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0", "customer-42");

        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns(correlations.ToList());

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var refs = call.Arg<IReadOnlyList<RelatedInstanceRef>>();
                IReadOnlyList<RelatedInstanceSnapshot> snapshots =
                    refs.Select(r => Snapshot(r.InstanceId, r.Flow)).ToList();
                return Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots));
            });

        var accessor = new RelatedInstanceAccessor(
            instance,
            reader,
            correlationRepository,
            new RelatedAccessOptions(),
            NullLogger.Instance);

        return (accessor, reader);
    }

    [Fact]
    public async Task SubAsync_ShouldReturnNull_WhenNoCorrelationMatchesTheKey()
    {
        var (accessor, _) = CreateAccessor(
            Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1)));

        var result = await accessor.SubAsync("doc-upload", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SubAsync_ShouldPickTheNewestCorrelationForTheKey()
    {
        var older = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var newer = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var (accessor, _) = CreateAccessor(
            Correlation(older, "doc-upload", new DateTime(2026, 1, 1)),
            Correlation(newer, "doc-upload", new DateTime(2026, 3, 1)));

        var result = await accessor.SubAsync("doc-upload", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.InstanceId.ShouldBe(newer);
    }

    [Fact]
    public async Task SubAsync_ShouldFindCompletedCorrelations()
    {
        var subId = Guid.NewGuid();
        var (accessor, _) = CreateAccessor(
            Correlation(subId, "kyc-flow", new DateTime(2026, 1, 1), completed: true));

        var result = await accessor.SubAsync("kyc-flow", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.CorrelationCompleted.ShouldBe(true);
        result.TerminalOutcome.ShouldBe("Completed");
    }

    [Fact]
    public async Task SubAsync_ShouldKeepInstanceStatusAndCorrelationStateIndependent()
    {
        // Subflow completion window: the sub instance is Completed while the correlation is still open.
        var subId = Guid.NewGuid();
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0");
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([Correlation(subId, "kyc-flow", new DateTime(2026, 1, 1), completed: false)]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(
                (IReadOnlyList<RelatedInstanceSnapshot>)[Snapshot(subId, "kyc-flow", "C", isCompleted: true)])));

        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        var result = await accessor.SubAsync("kyc-flow", CancellationToken.None);

        result!.IsCompleted.ShouldBeTrue();          // instance reached C
        result.CorrelationCompleted.ShouldBe(false); // relationship still open
        result.TerminalOutcome.ShouldBeNull();
    }

    [Fact]
    public async Task SubAsync_ShouldExposeSubFlowTypeCode()
    {
        var subId = Guid.NewGuid();
        var (accessor, _) = CreateAccessor(
            Correlation(subId, "notify-flow", new DateTime(2026, 1, 1), subFlowType: "P"));

        var result = await accessor.SubAsync("notify-flow", CancellationToken.None);

        result!.SubFlowType.ShouldBe("P");
    }

    [Fact]
    public async Task SubsAsync_ShouldReturnEveryCorrelationOrderedByCreatedAt()
    {
        var first = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        var second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var (accessor, _) = CreateAccessor(
            Correlation(second, "doc-upload", new DateTime(2026, 3, 1)),
            Correlation(first, "kyc-flow", new DateTime(2026, 1, 1)));

        var result = await accessor.SubsAsync(null, CancellationToken.None);

        result.Select(r => r.InstanceId).ShouldBe([first, second]);
    }

    [Fact]
    public async Task SubsAsync_ShouldFilterByKey()
    {
        var (accessor, _) = CreateAccessor(
            Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 2, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 3, 1)));

        var result = await accessor.SubsAsync("doc-upload", CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldAllBe(r => r.Flow == "doc-upload");
    }

    [Fact]
    public async Task SubsAsync_ShouldBatchReadsInOneCall()
    {
        var (accessor, reader) = CreateAccessor(
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 1, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 2, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 3, 1)));

        await accessor.SubsAsync("doc-upload", CancellationToken.None);

        await reader.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs => refs.Count == 3),
            Arg.Any<CancellationToken>());
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task SubsAsync_ShouldOmitInstancesTheReaderDoesNotReturn()
    {
        var present = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
        var missing = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0");
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([
                Correlation(present, "doc-upload", new DateTime(2026, 1, 1)),
                Correlation(missing, "doc-upload", new DateTime(2026, 2, 1))
            ]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(
                (IReadOnlyList<RelatedInstanceSnapshot>)[Snapshot(present, "doc-upload")])));

        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        var result = await accessor.SubsAsync("doc-upload", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].InstanceId.ShouldBe(present);
    }

    [Fact]
    public async Task SubsAsync_ShouldThrow_WhenReaderFails()
    {
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0");
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 1, 1))]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(
                Error.Failure("RELATED_READ", "compliance domain unreachable"))));

        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        var exception = await Should.ThrowAsync<RelatedInstanceAccessException>(
            () => accessor.SubsAsync("doc-upload", CancellationToken.None));

        exception.Message.ShouldContain("compliance domain unreachable");
    }

    [Fact]
    public async Task SubKeysAsync_ShouldReturnDistinctKeysWithoutReadingData()
    {
        var (accessor, reader) = CreateAccessor(
            Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 2, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 3, 1)));

        var keys = await accessor.SubKeysAsync(CancellationToken.None);

        keys.ShouldBe(["kyc-flow", "doc-upload"]);
        await reader.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default);
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task CorrelationList_ShouldBeLoadedOnlyOnce()
    {
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1))]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var refs = call.Arg<IReadOnlyList<RelatedInstanceRef>>();
                IReadOnlyList<RelatedInstanceSnapshot> snapshots =
                    refs.Select(r => Snapshot(r.InstanceId, r.Flow)).ToList();
                return Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots));
            });

        var accessor = new RelatedInstanceAccessor(
            Instance.Create(ParentId, "loan-application", "2.1.0"),
            reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.SubKeysAsync(CancellationToken.None);
        await accessor.SubAsync("kyc-flow", CancellationToken.None);
        await accessor.SubsAsync(null, CancellationToken.None);

        await correlationRepository.Received(1).GetByParentAsync(ParentId, Arg.Any<CancellationToken>());
    }
}
```

If `correlation.CreatedAt = createdAt;` does not compile (setter not public), replace those two lines
with a reflection helper placed at the bottom of the test class:

```csharp
    private static void ForceCreatedAt(InstanceCorrelation correlation, DateTime createdAt) =>
        typeof(InstanceCorrelation)
            .GetProperty(nameof(InstanceCorrelation.CreatedAt))!
            .SetValue(correlation, createdAt);
```

and call `ForceCreatedAt(correlation, createdAt);` instead of the direct assignment.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~RelatedInstanceAccessorSubTests"`

Expected: FAIL — `System.NotImplementedException: Implemented in Task 4.`

- [ ] **Step 3: Replace the three stub members in `RelatedInstanceAccessor.cs`**

Add these next to the existing fields:

```csharp
    private const string DirectionSub = "sub";

    private readonly CorrelationCache _correlationCache;

    /// <summary>
    /// The lazily loaded correlation list plus its load gate, held in one object so parallel task
    /// branches created by <c>ForBranch</c> can share a single load instead of each re-issuing
    /// <c>GetByParentAsync</c> for the same parent instance.
    /// </summary>
    private sealed class CorrelationCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public List<InstanceCorrelation>? Items;
    }
```

Both constructors gain a trailing `CorrelationCache? correlationCache = null` parameter, and the
private one assigns `_correlationCache = correlationCache ?? new CorrelationCache();`. The public
constructor passes nothing, so a standalone accessor always gets a fresh cache.

Replace the three `NotImplementedException` members with:

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default)
    {
        var correlations = await GetCorrelationsAsync(cancellationToken);
        return correlations
            .Select(correlation => correlation.SubFlowName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<RelatedInstanceView?> SubAsync(
        string subFlowKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subFlowKey);

        var correlations = await GetCorrelationsAsync(cancellationToken);
        var correlation = correlations
            .Where(candidate => string.Equals(candidate.SubFlowName, subFlowKey, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();

        if (correlation == null)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, DirectionSub, subFlowKey);
            return null;
        }

        return await ResolveAsync(ToRef(correlation), DirectionSub, correlation, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default)
    {
        var correlations = await GetCorrelationsAsync(cancellationToken);
        var matches = correlations
            .Where(candidate => subFlowKey == null ||
                                string.Equals(candidate.SubFlowName, subFlowKey, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.CreatedAt)
            .ToList();

        if (matches.Count == 0)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, DirectionSub, subFlowKey);
            return [];
        }

        return await ResolveManyAsync(matches, cancellationToken);
    }

    private async Task<IReadOnlyList<RelatedInstanceView>> ResolveManyAsync(
        List<InstanceCorrelation> correlations,
        CancellationToken cancellationToken)
    {
        var pending = correlations
            .Where(correlation => !_memo.ContainsKey(correlation.SubFlowInstanceId))
            .ToList();

        if (pending.Count > 0)
        {
            EnsureUnderLimit(pending.Count);

            var references = pending.Select(ToRef).ToList();
            var result = await _reader.ReadManyAsync(references, cancellationToken);
            if (!result.IsSuccess)
            {
                var reason = result.Error.Message ?? "unknown";
                // Batch-shaped log: a batch can span several domains, so naming any single
                // correlation's domain would point an operator at an innocent target.
                var domains = string.Join(
                    ", ",
                    pending.Select(correlation => correlation.SubFlowDomain).Distinct(StringComparer.Ordinal));
                _logger.RelatedInstanceBatchResolutionFailed(_instance.Id, pending.Count, domains, reason);
                throw new RelatedInstanceAccessException(
                    $"Failed to read {pending.Count} related instance(s) of {_instance.Id} " +
                    $"in domain(s) {domains}: {reason}");
            }

            var byId = result.Value!.ToDictionary(snapshot => snapshot.InstanceId);
            foreach (var correlation in pending)
            {
                if (!byId.TryGetValue(correlation.SubFlowInstanceId, out var snapshot))
                {
                    _logger.RelatedInstanceNotFound(_instance.Id, DirectionSub, correlation.SubFlowName);
                    continue;
                }

                _memo[correlation.SubFlowInstanceId] = ToView(snapshot, correlation);
                _logger.RelatedInstanceResolved(
                    _instance.Id, DirectionSub, snapshot.InstanceId, snapshot.Domain, snapshot.Flow);
            }
        }

        return correlations
            .Where(correlation => _memo.ContainsKey(correlation.SubFlowInstanceId))
            .Select(correlation => _memo[correlation.SubFlowInstanceId])
            .ToList();
    }

    private async Task<List<InstanceCorrelation>> GetCorrelationsAsync(CancellationToken cancellationToken)
    {
        if (_correlationCache.Items != null)
            return _correlationCache.Items;

        await _correlationCache.Gate.WaitAsync(cancellationToken);
        try
        {
            // Deliberately not Instance.ChildCorrelations: the repository's default include filters
            // out completed correlations, and completed subflow output must stay readable.
            _correlationCache.Items ??=
                await _correlationRepository.GetByParentAsync(_instance.Id, cancellationToken);
            return _correlationCache.Items;
        }
        finally
        {
            _correlationCache.Gate.Release();
        }
    }

    private static RelatedInstanceRef ToRef(InstanceCorrelation correlation) =>
        new(
            correlation.SubFlowInstanceId,
            correlation.SubFlowDomain,
            correlation.SubFlowName,
            correlation.SubFlowVersion);
```

Change `EnsureUnderLimit` to accept a count, and update the existing single-read call site in
`ResolveAsync` from `EnsureUnderLimit();` to `EnsureUnderLimit(1);`:

```csharp
    private void EnsureUnderLimit(int additional)
    {
        if (_memo.Count + additional <= _options.MaxResolutionsPerContext)
            return;

        _logger.RelatedInstanceResolutionLimitExceeded(_instance.Id, _options.MaxResolutionsPerContext);
        throw new RelatedInstanceAccessException(
            $"Related instance resolution limit of {_options.MaxResolutionsPerContext} exceeded for instance {_instance.Id}. " +
            $"Attempted to add {additional} more. " +
            "Reduce the number of distinct related instances a single script resolves, or raise " +
            $"{RelatedAccessOptions.SectionName}:MaxResolutionsPerContext.");
    }
```

Add `using System.Linq;` if the file does not already have it.

- [ ] **Step 4: Run both accessor test classes to verify they pass**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~RelatedInstanceAccessor"`

Expected: PASS — 14 parent tests + 15 sub tests.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorSubTests.cs
git commit -m "feat(scripting): resolve correlation instance data including completed correlations"
```

---

## Task 5: Accessor — memoization, cap, and branch sharing

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs` (add `ForBranch` and `ClearMemo`)
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorMemoAndLimitTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorMemoAndLimitTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Data;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class RelatedInstanceAccessorMemoAndLimitTests
{
    private static readonly Guid InstanceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ParentId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static Instance ChildWithParent()
    {
        var instance = Instance.Create(InstanceId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = "lending",
            [DomainConsts.MetaDataKeys.Flow] = "loan-application",
            [DomainConsts.MetaDataKeys.Version] = "2.1.0"
        });
        return instance;
    }

    private static IRelatedInstanceReader ReaderReturning(Guid id)
    {
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(new RelatedInstanceSnapshot
            {
                InstanceId = id,
                Domain = "lending",
                Flow = "loan-application",
                Status = "A"
            }));
        return reader;
    }

    [Fact]
    public async Task ParentAsync_ShouldReadOnceAndServeTheMemoAfterwards()
    {
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        var first = await accessor.ParentAsync(CancellationToken.None);
        var second = await accessor.ParentAsync(CancellationToken.None);

        first.ShouldBeSameAs(second);
        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolution_ShouldThrow_WhenCapIsExceeded()
    {
        var instance = Instance.Create(InstanceId, "loan-application", "2.1.0");

        var correlations = Enumerable.Range(0, 3)
            .Select(index => InstanceCorrelation.Create(
                Guid.NewGuid(), InstanceId, "awaiting-sub",
                Guid.Parse($"dddddddd-0000-0000-0000-00000000000{index}"),
                "P", "compliance", $"flow-{index}", "1.0.0"))
            .ToList();

        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(correlations);

        var reader = Substitute.For<IRelatedInstanceReader>();
        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository,
            new RelatedAccessOptions { MaxResolutionsPerContext = 2 },
            NullLogger.Instance);

        var exception = await Should.ThrowAsync<RelatedInstanceAccessException>(
            () => accessor.SubsAsync(null, CancellationToken.None));

        exception.Message.ShouldContain("limit of 2");
        await reader.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default);
    }

    [Fact]
    public async Task ForBranch_ShouldShareTheMemoWithTheOriginal()
    {
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.ParentAsync(CancellationToken.None);
        var branch = accessor.ForBranch(ChildWithParent());
        await branch.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearMemo_ShouldForceTheNextReadToHitTheReader()
    {
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.ParentAsync(CancellationToken.None);
        accessor.ClearMemo();
        await accessor.ParentAsync(CancellationToken.None);

        await reader.Received(2).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~RelatedInstanceAccessorMemoAndLimitTests"`

Expected: build FAILS — `CS1061: 'RelatedInstanceAccessor' does not contain a definition for 'ForBranch'`.

- [ ] **Step 3: Add `ForBranch` and `ClearMemo` to `RelatedInstanceAccessor.cs`**

Add both public members after `SubsAsync`:

```csharp
    /// <summary>
    /// Creates an accessor for a parallel task branch. The branch is bound to its own instance
    /// snapshot but shares this accessor's memo and reader, so a related instance already resolved by
    /// the coordinator is not read again. Safe because branches only read.
    /// </summary>
    /// <remarks>
    /// The correlation cache is shared too, but only when the branch snapshots the same instance —
    /// which is what <c>ScriptContext.CreateParallelBranch</c> does. Without that, every branch that
    /// touches a <c>Sub*</c> member would re-issue <c>GetByParentAsync</c> for the same parent id.
    /// A branch bound to a different instance gets its own cache, because its correlations differ.
    /// </remarks>
    /// <param name="branchInstance">The branch's instance snapshot.</param>
    internal RelatedInstanceAccessor ForBranch(Instance branchInstance) =>
        new(
            branchInstance,
            _reader,
            _correlationRepository,
            _options,
            _logger,
            _memo,
            branchInstance.Id == _instance.Id ? _correlationCache : null);

    /// <summary>
    /// Drops every memoized related instance and the cached correlation list. Called when the owning
    /// ScriptContext is disposed so the resolved data does not outlive the transition.
    /// </summary>
    internal void ClearMemo()
    {
        _memo.Clear();
        _correlationCache.Items = null;
    }
```

`ForBranch` and `ClearMemo` are `internal`, not `public`: they are lifecycle operations owned by
`ScriptContext` (same assembly), and `IRelatedInstanceAccessor` — the script-facing surface —
deliberately omits both. `BBT.Workflow.Domain.csproj` already grants `InternalsVisibleTo` to
`BBT.Workflow.Domain.Tests` and `BBT.Workflow.Infrastructure`, so the tests still reach them.

- [ ] **Step 4: Run all accessor tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~RelatedInstanceAccessor"`

Expected: PASS — 14 + 16 + 7 tests (plus the 5 NullRelatedInstanceAccessor tests the same filter picks up).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Related/RelatedInstanceAccessor.cs test/BBT.Workflow.Domain.Tests/Scripting/Related/RelatedInstanceAccessorMemoAndLimitTests.cs
git commit -m "feat(scripting): add memo sharing for parallel branches and memo reset"
```

---

## Task 6: Expose `Related` on ScriptContext

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Models.cs` (`ScriptContext`: new property, `Dispose`, `CreateParallelBranch`, `Builder`)
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/Related/ScriptContextRelatedTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Domain.Tests/Scripting/Related/ScriptContextRelatedTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Data;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class ScriptContextRelatedTests
{
    private static readonly Guid InstanceId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ParentId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static Instance ChildWithParent()
    {
        var instance = Instance.Create(InstanceId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = "lending",
            [DomainConsts.MetaDataKeys.Flow] = "loan-application"
        });
        return instance;
    }

    private static RelatedInstanceAccessor Accessor(Instance instance, IRelatedInstanceReader reader) =>
        new(instance, reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

    private static IRelatedInstanceReader OkReader()
    {
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(new RelatedInstanceSnapshot
            {
                InstanceId = ParentId,
                Domain = "lending",
                Flow = "loan-application",
                Status = "A"
            }));
        return reader;
    }

    [Fact]
    public void Related_ShouldDefaultToTheNullAccessor()
    {
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>()).Build();

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
        context.Related.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task Related_ShouldExposeTheConfiguredAccessor()
    {
        var instance = ChildWithParent();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, OkReader()))
            .Build();

        context.Related.HasParent.ShouldBeTrue();
        var parent = await context.Related.ParentAsync(CancellationToken.None);
        parent!.InstanceId.ShouldBe(ParentId);
    }

    [Fact]
    public async Task CreateParallelBranch_ShouldShareTheAccessorMemo()
    {
        var instance = ChildWithParent();
        var reader = OkReader();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, reader))
            .Build();

        await context.Related.ParentAsync(CancellationToken.None);
        var branch = context.CreateParallelBranch();
        await branch.Related.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispose_ShouldClearTheMemoAndResetToTheNullAccessor()
    {
        var instance = ChildWithParent();
        var reader = OkReader();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, reader))
            .Build();

        await context.Related.ParentAsync(CancellationToken.None);
        context.Dispose();

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptContextRelatedTests"`

Expected: build FAILS — `CS1061: 'ScriptContext' does not contain a definition for 'Related'`.

- [ ] **Step 3: Add the property to `ScriptContext`**

In `src/BBT.Workflow.Domain/Scripting/Models.cs`, add `using BBT.Workflow.Scripting.Related;` to the
using block, then insert this property immediately after the existing `Incident` property:

```csharp
    /// <summary>
    /// Access to instances related to <see cref="Instance"/> — one hop up (the parent that started this
    /// instance as a SubFlow/SubProcess) or one hop down (this instance's own correlations).
    /// Nothing is pre-fetched; the first call that needs data performs the read, and results are
    /// memoized until this context is disposed.
    /// </summary>
    /// <remarks>
    /// Reads are unfiltered by design (no query-role check, no x-roles field filtering, no extensions).
    /// Copying a related instance's field into this instance's data therefore makes that field reachable
    /// by any client entitled to read this instance — x-roles protection does not follow the copy, so
    /// copy only the fields you intend to expose. Every cross-domain read is logged
    /// (<c>RelatedInstanceCrossDomainRead</c>, event id 20432).
    /// Never null: defaults to <see cref="NullRelatedInstanceAccessor"/> when no reader is wired.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
    public IRelatedInstanceAccessor Related
    {
        get
        {
            // Deliberately guarded, unlike Body/Incident which merely go null. Those are nullable
            // values; this is an accessor whose contract is "null means no parent". Handing back the
            // null accessor after disposal would answer HasParent == false — a definite claim, not an
            // absence — which is exactly the fault-as-absence conflation §5.5 forbids.
            ThrowIfDisposed();
            return _related;
        }
        private set => _related = value;
    }

    private IRelatedInstanceAccessor _related = NullRelatedInstanceAccessor.Instance;
```

- [ ] **Step 4: Clear the memo on dispose**

In `Dispose(bool disposing)`, inside the `try` block, after `Incident = null;` add:

```csharp
                // Backing field, not the property: the property getter throws once disposed, and
                // Dispose must stay callable twice.
                if (_related is RelatedInstanceAccessor accessor)
                    accessor.ClearMemo();

                _related = NullRelatedInstanceAccessor.Instance;
```

- [ ] **Step 5: Propagate to parallel branches**

In `CreateParallelBranch()`, after the object-initializer block that creates `branch` and after the
existing `if (branch.Instance != null) { ... }` incident block, add:

```csharp
        // A real accessor is bound to a specific instance, so a branch without one must not inherit
        // it — that would answer the branch's questions from the coordinator's instance.
        branch.Related = (Related, branch.Instance) switch
        {
            (RelatedInstanceAccessor branchSource, not null) => branchSource.ForBranch(branch.Instance),
            (RelatedInstanceAccessor, null) => NullRelatedInstanceAccessor.Instance,
            _ => Related
        };
```

- [ ] **Step 6: Add the builder setter**

In the nested `public sealed class Builder`, add after `SetInstance`:

```csharp
        /// <summary>
        /// Sets the related-instance accessor. When omitted, the context uses
        /// <see cref="NullRelatedInstanceAccessor"/> and reports no parent and no correlations.
        /// </summary>
        public Builder SetRelated(IRelatedInstanceAccessor? related)
        {
            if (related != null)
                _context.Related = related;

            return this;
        }
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptContextRelatedTests"`

Expected: PASS, 8 tests.

- [ ] **Step 8: Verify no existing ScriptContext test regressed**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~ScriptContext"`

Expected: PASS — the pre-existing `ScriptContextTests` and `ScriptContextRawBodyTests` keep working
because `Related` defaults to the null accessor.

- [ ] **Step 9: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Models.cs test/BBT.Workflow.Domain.Tests/Scripting/Related/ScriptContextRelatedTests.cs
git commit -m "feat(scripting): expose Related accessor on ScriptContext"
```

---

## Task 7: Wire the accessor in ScriptContextBuilder

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs`

No new test: `ScriptContextBuilder` is `internal sealed` and DI-constructed; its behaviour here is
covered end-to-end once the readers are registered (Task 11) and by the accessor tests above. The
optional constructor parameters keep every existing construction site compiling.

- [ ] **Step 1: Add the dependencies**

Change the primary constructor to:

```csharp
internal sealed class ScriptContextBuilder(
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    ILogger<ScriptContext> logger,
    ILogger<RelatedInstanceAccessor> relatedLogger,
    IRequestRawBodyProvider? rawBodyProvider = null,
    IRelatedInstanceReader? relatedInstanceReader = null,
    IInstanceCorrelationRepository? correlationRepository = null,
    IOptions<RelatedAccessOptions>? relatedAccessOptions = null) : IScriptContextBuilder
```

Add these usings:

```csharp
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Options;
```

- [ ] **Step 2: Build the accessor in `BuildAsync`**

In `BuildAsync`, replace the existing instance block:

```csharp
        // Resolve instance if needed
        var instance = await ResolveInstanceAsync(cancellationToken);
        if (instance != null)
        {
            builder.SetInstance(instance);
        }
```

with:

```csharp
        // Resolve instance if needed
        var instance = await ResolveInstanceAsync(cancellationToken);
        if (instance != null)
        {
            builder.SetInstance(instance);
            builder.SetRelated(BuildRelatedAccessor(instance));
        }
```

- [ ] **Step 3: Add the factory method**

Add at the end of the class, next to the other private helpers:

```csharp
    /// <summary>
    /// Builds the related-instance accessor for the resolved instance. Returns null when the reader or
    /// the correlation repository is not registered — the ScriptContext then falls back to the no-op
    /// accessor, which is the correct behaviour for unit tests and reader-less hosts.
    /// </summary>
    private IRelatedInstanceAccessor? BuildRelatedAccessor(Instance instance)
    {
        if (relatedInstanceReader == null || correlationRepository == null)
            return null;

        return new RelatedInstanceAccessor(
            instance,
            relatedInstanceReader,
            correlationRepository,
            relatedAccessOptions?.Value ?? new RelatedAccessOptions(),
            relatedLogger);
    }
```

- [ ] **Step 4: Verify it compiles and Domain tests still pass**

Run: `dotnet build src/BBT.Workflow.Domain && dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~Scripting"`

Expected: `Build succeeded`, and the Scripting tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs
git commit -m "feat(scripting): construct related instance accessor in ScriptContextBuilder"
```

---

## Task 8: Application-layer local read

**Files:**
- Create: `src/BBT.Workflow.Application/Instances/Related/IRelatedInstanceQueryAppService.cs`
- Create: `src/BBT.Workflow.Application/Instances/Related/RelatedInstanceQueryAppService.cs`
- Test: `test/BBT.Workflow.Application.Tests/Instances/Related/RelatedInstanceQueryAppServiceTests.cs`

This is the layer that must **not** apply authorization. Compare with
`InstanceQueryAppService.GetInstanceDataAsync`, which calls `IsInstanceQueryAllowedAsync` and applies
`x-roles` filtering — this service does neither, and does not run extensions or touch the data-function
response cache.

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Application.Tests/Instances/Related/RelatedInstanceQueryAppServiceTests.cs`:

```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Json;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Related;

public class RelatedInstanceQueryAppServiceTests
{
    private static readonly Guid TargetId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static RelatedInstanceRef Reference() =>
        new(TargetId, "lending", "loan-application", "2.1.0");

    private static Instance TargetInstance()
    {
        var instance = Instance.Create(TargetId, "loan-application", "2.1.0", "customer-42");
        instance.AddData(
            Guid.NewGuid(),
            new JsonData(JsonSerializer.SerializeToElement(new
            {
                creditLimit = 50000,
                restrictedField = "only-for-officers"
            })));
        return instance;
    }

    private static RelatedInstanceQueryAppService CreateService(Instance? instance)
    {
        var repository = Substitute.For<IInstanceRepository>();
        repository.FindByIdentifierAsReadOnlyAsync(TargetId.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);
        return new RelatedInstanceQueryAppService(repository, NullLogger<RelatedInstanceQueryAppService>.Instance);
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnSuccessWithNull_WhenInstanceDoesNotExist()
    {
        var service = CreateService(null);

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_ShouldProjectIdentityAndStatus()
    {
        var service = CreateService(TargetInstance());

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var snapshot = result.Value.ShouldNotBeNull();
        snapshot.InstanceId.ShouldBe(TargetId);
        snapshot.Key.ShouldBe("customer-42");
        snapshot.Domain.ShouldBe("lending");
        snapshot.Flow.ShouldBe("loan-application");
        snapshot.FlowVersion.ShouldBe("2.1.0");
        snapshot.Status.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnDataUnfiltered()
    {
        var service = CreateService(TargetInstance());

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        // No x-roles filtering: every field of the stored payload survives, including one a caller
        // without the right role could not see through the Data function.
        var data = (IDictionary<string, object?>)result.Value!.Data!;
        data.ShouldContainKey("creditLimit");
        data.ShouldContainKey("restrictedField");
    }

    [Fact]
    public async Task ReadAsync_ShouldNotRequireRolesOrHeaders()
    {
        // Regression guard: the internal read path must work with no caller identity at all,
        // which is the situation in scheduled, automatic, event and background-job contexts.
        var service = CreateService(TargetInstance());

        var result = await service.ReadAsync(Reference(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadManyAsync_ShouldOmitMissingInstances()
    {
        var missingId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var repository = Substitute.For<IInstanceRepository>();
        repository.FindByIdentifierAsReadOnlyAsync(TargetId.ToString(), Arg.Any<CancellationToken>())
            .Returns(TargetInstance());
        repository.FindByIdentifierAsReadOnlyAsync(missingId.ToString(), Arg.Any<CancellationToken>())
            .Returns((Instance?)null);
        var service = new RelatedInstanceQueryAppService(
            repository, NullLogger<RelatedInstanceQueryAppService>.Instance);

        var result = await service.ReadManyAsync(
            [Reference(), new RelatedInstanceRef(missingId, "lending", "loan-application", "2.1.0")],
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(1);
        result.Value[0].InstanceId.ShouldBe(TargetId);
    }
}
```

If `JsonData`'s constructor signature differs, copy the exact construction used by an existing test:

```bash
grep -rn "new JsonData(" test/BBT.Workflow.Domain.Tests | head -3
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~RelatedInstanceQueryAppServiceTests"`

Expected: build FAILS — `CS0246: ... 'RelatedInstanceQueryAppService' could not be found`.

- [ ] **Step 3: Create `IRelatedInstanceQueryAppService.cs`**

```csharp
using BBT.Aether.Results;
using BBT.Workflow.Scripting.Related;

namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Local, system-identity read of another instance's latest data, for related-instance access from
/// mapping scripts. Deliberately bypasses the query-role check, x-roles field filtering, extensions
/// and the data-function response cache that <see cref="IInstanceQueryAppService"/> applies — the read
/// happens inside the engine's own correlation frame, not on behalf of a caller.
/// </summary>
public interface IRelatedInstanceQueryAppService
{
    /// <summary>Reads one instance. A successful result carrying null means the instance was not found.</summary>
    Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default);

    /// <summary>Reads several instances in the current schema, omitting the ones that do not exist.</summary>
    Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Create `RelatedInstanceQueryAppService.cs`**

```csharp
using BBT.Aether.Results;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Reads related instances from the current schema with no authorization filtering.
/// Registered per-scope and always invoked inside an established schema scope by
/// <c>LocalRelatedInstanceReader</c>.
/// </summary>
public sealed class RelatedInstanceQueryAppService(
    IInstanceRepository instanceRepository,
    ILogger<RelatedInstanceQueryAppService> logger) : IRelatedInstanceQueryAppService
{
    /// <inheritdoc />
    public async Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        try
        {
            var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
                reference.InstanceId.ToString(), cancellationToken);

            return Result<RelatedInstanceSnapshot?>.Ok(
                instance == null ? null : ToSnapshot(instance, reference));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller itself went away — propagate rather than reporting a read failure. Matches
            // WorkflowOutputMappingService and the background job handlers.
            throw;
        }
        catch (Exception exception)
        {
            // WorkflowLogs extension, not raw logger.LogError — the coding standard forbids the latter.
            logger.RelatedInstanceReadFailed(exception, reference.InstanceId, reference.Flow);

            return Result<RelatedInstanceSnapshot?>.Fail(Error.Failure(
                WorkflowErrorCodes.RelatedInstanceReadFailed,
                $"Related instance read failed for {reference.InstanceId}: {exception.Message}",
                detail: exception.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (references.Count == 0)
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]);

        try
        {
            // One query, not one per reference: this is the whole reason the batch API exists, and
            // the project rule forbids N+1. The reads also skip the ChildCorrelations include that
            // FindByIdentifierAsReadOnlyAsync carries — this service never looks at correlations.
            var instances = await instanceRepository.FindByIdsAsReadOnlyAsync(
                references.Select(reference => reference.InstanceId).ToList(),
                cancellationToken);

            var byId = instances.ToDictionary(instance => instance.Id);

            // Reference order is preserved, and references with no matching row are omitted —
            // absence is data. Only a thrown exception fails the batch.
            var snapshots = references
                .Where(reference => byId.ContainsKey(reference.InstanceId))
                .Select(reference => ToSnapshot(byId[reference.InstanceId], reference))
                .ToList();

            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.RelatedInstanceReadFailed(exception, references[0].InstanceId, references[0].Flow);

            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(Error.Failure(
                WorkflowErrorCodes.RelatedInstanceReadFailed,
                $"Related instance batch read of {references.Count} instance(s) failed: {exception.Message}",
                detail: exception.GetType().Name));
        }
    }

    /// <summary>
    /// Projects the aggregate into the wire/read shape. <paramref name="reference"/> supplies the
    /// domain: the <see cref="Instance"/> aggregate does not carry one (the schema and runtime do), and
    /// the reference is what the caller resolved the instance by, so it is authoritative.
    /// </summary>
    private static RelatedInstanceSnapshot ToSnapshot(Instance instance, RelatedInstanceRef reference) => new()
    {
        InstanceId = instance.Id,
        Key = instance.Key,
        Domain = reference.Domain,
        Flow = instance.Flow,
        FlowVersion = instance.FlowVersion,
        Status = instance.Status.Code,
        CurrentState = instance.CurrentState,
        IsCompleted = instance.IsCompleted,
        Data = instance.Data
    };
}
```

Add a dedicated error code to `src/BBT.Workflow.Domain/WorkflowErrorCodes.cs`, next to the other
`Instance:1000xx` codes (100033 is the next free value — verify before using):

```csharp
    public const string RelatedInstanceReadFailed = "Instance:100033";
```

Do **not** reuse `InstanceNotFound` (`Instance:100017`). Genuine not-found is already reported as
`Result.Ok(null)`; reusing that code for an infrastructure fault would let a caller or an alert conflate
"the related instance does not exist" with "the read blew up" — the exact ambiguity the
absence-vs-failure split exists to prevent.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~RelatedInstanceQueryAppServiceTests"`

Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Application/Instances/Related test/BBT.Workflow.Application.Tests/Instances/Related
git commit -m "feat(instances): add unfiltered related instance read app service"
```

---

## Task 9: Internal HTTP endpoints

**Files:**
- Modify: `src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs`

These sit alongside the existing internal-to-internal endpoints on the same controller
(`sub/state`, `sub/fault`, `child-cancel`, `child-fault`, `longpoll/ack`). Follow whatever those do for
Swagger grouping and attributes — read one of them first:

```bash
grep -n -B8 -A25 'instances/{instance}/child-cancel' orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs
```

- [ ] **Step 1: Add the URL templates**

The file keeps route shapes in `public const string *Template` fields (numbered `{0}` placeholders) and
composes them through a private `BuildUrl(template, apiVersionPrefix, params object[] args)`.

Add the two constants after `LongPollAckTemplate` (line ~149):

```csharp
    /// <summary>
    /// Internal related-instance data read template.
    /// {0} = domain, {1} = workflow, {2} = instance
    /// </summary>
    public const string RelatedDataTemplate = "/{0}/workflows/{1}/instances/{2}/internal/related-data";

    /// <summary>
    /// Internal batched related-instance data read template.
    /// {0} = domain, {1} = workflow
    /// </summary>
    public const string RelatedDataBatchTemplate = "/{0}/workflows/{1}/internal/related-data/batch";
```

Add the two methods after `ChildCancel` (line ~391):

```csharp
    /// <summary>
    /// Generates URL for the internal related-instance data read endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string RelatedData(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(RelatedDataTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for the internal batched related-instance data read endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string RelatedDataBatch(string domain, string workflow, string? apiVersionPrefix = null)
        => BuildUrl(RelatedDataBatchTemplate, apiVersionPrefix, domain, workflow);
```

- [ ] **Step 2: Add the request DTO**

Create the batch request body type in the same folder as the controller's other input DTOs (find it
with `ls orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/`). If the
controller uses Application-layer DTOs, put it in
`src/BBT.Workflow.Application/Instances/Related/RelatedDataBatchInput.cs`:

```csharp
namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Body of the internal batched related-data read. All ids must belong to the routed domain and flow.
/// </summary>
public sealed class RelatedDataBatchInput
{
    /// <summary>Instance identifiers to read. Ids that do not resolve are omitted from the response.</summary>
    public IReadOnlyList<Guid> InstanceIds { get; init; } = [];
}
```

- [ ] **Step 3: Add the two endpoints**

In `InstanceController.cs`, after the `child-fault` action, add:

```csharp
    /// <summary>
    /// Reads a single instance's raw data for related-instance access from another runtime.
    /// Internal-to-internal: no caller identity, no query-role check, no x-roles field filtering and
    /// no extensions. Never expose this route publicly.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow key.</param>
    /// <param name="instance">Target instance identifier.</param>
    /// <param name="version">Target workflow version, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instance snapshot, or 404 when it does not exist.</returns>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/internal/related-data")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> GetRelatedDataAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] string? version,
        CancellationToken cancellationToken)
    {
        var result = await relatedInstanceQueryAppService.ReadAsync(
            new RelatedInstanceRef(instance, domain, workflow, version),
            cancellationToken);

        if (!result.IsSuccess)
            return Problem(detail: result.Error.Message, statusCode: StatusCodes.Status500InternalServerError);

        return result.Value == null ? NotFound() : Ok(result.Value);
    }

    /// <summary>
    /// Reads several instances' raw data in one call for related-instance access from another runtime.
    /// Internal-to-internal, same caveats as the single read. Ids that do not resolve are omitted.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow key.</param>
    /// <param name="input">Instance identifiers to read.</param>
    /// <param name="version">Target workflow version, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshots that resolved.</returns>
    [HttpPost("{domain}/workflows/{workflow}/internal/related-data/batch")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> GetRelatedDataBatchAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromBody] RelatedDataBatchInput input,
        [FromQuery] string? version,
        CancellationToken cancellationToken)
    {
        var references = input.InstanceIds
            .Select(id => new RelatedInstanceRef(id, domain, workflow, version))
            .ToList();

        var result = await relatedInstanceQueryAppService.ReadManyAsync(references, cancellationToken);

        if (!result.IsSuccess)
            return Problem(detail: result.Error.Message, statusCode: StatusCodes.Status500InternalServerError);

        return Ok(result.Value);
    }
```

Add `IRelatedInstanceQueryAppService relatedInstanceQueryAppService` to the controller's primary
constructor parameter list, and add `using BBT.Workflow.Instances.Related;` plus
`using BBT.Workflow.Scripting.Related;`.

- [ ] **Step 4: Register the app service**

Find where the other Application services are registered and add the scoped registration. Locate it with:

```bash
grep -rn "IInstanceQueryAppService, InstanceQueryAppService" src/ orchestration/
```

Add next to that line:

```csharp
        services.AddScoped<IRelatedInstanceQueryAppService, RelatedInstanceQueryAppService>();
```

- [ ] **Step 5: Verify the host builds and the routes are registered**

Run: `dotnet build orchestration/BBT.Workflow.Orchestration.HttpApi.Host`

Expected: `Build succeeded`.

Then confirm the routes exist and are hidden from Swagger:

```bash
grep -n "internal/related-data" orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs
```

Expected: two matches, each preceded by `[ApiExplorerSettings(IgnoreApi = true)]`.

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Domain/Definitions/InstanceUrlTemplates.cs src/BBT.Workflow.Application/Instances/Related orchestration/BBT.Workflow.Orchestration.HttpApi.Host
git commit -m "feat(api): add internal related-data read endpoints"
```

---

## Task 10: Remote reader

**Files:**
- Create: `src/BBT.Workflow.Infrastructure/Gateway/RemoteRelatedInstanceReader.cs`

Model this on `src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceQueryAppService.cs` — read
its `GetInstanceDataAsync` first so the endpoint resolution, `HttpClient` usage and error mapping match:

```bash
sed -n '95,175p' src/BBT.Workflow.Infrastructure/Instances/Remote/RemoteInstanceQueryAppService.cs
```

Unlike that service, this reader does **not** take `ICurrentUser` and propagates no caller headers —
the call is system-identity by design.

- [ ] **Step 1: Create the file**

```csharp
using System.Net;
using System.Net.Http.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads related instances that live in another domain, over the internal related-data endpoints.
/// Sends no caller identity: related-instance access is system-identity by design.
/// </summary>
public sealed class RemoteRelatedInstanceReader(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver) : IRelatedInstanceReader
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <inheritdoc />
    public async Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default)
    {
        var endpointResult = await endpointResolver.GetEndpointAsync(
            reference.Domain, EndpointKind.Url, cancellationToken);

        if (!endpointResult.IsSuccess)
            return Result<RelatedInstanceSnapshot?>.Fail(endpointResult.Error);

        var relativePath = InstanceUrlTemplates.RelatedData(
            reference.Domain, reference.Flow, reference.InstanceId.ToString(), ApiVersionPrefix);

        var url = BuildUrl(endpointResult.Value!, relativePath, reference.FlowVersion);

        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result<RelatedInstanceSnapshot?>.Ok(null);

            if (!response.IsSuccessStatusCode)
                return Result<RelatedInstanceSnapshot?>.Fail(Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"Related instance read to {reference.Domain} returned {(int)response.StatusCode}."));

            var snapshot = await response.Content.ReadFromJsonAsync<RelatedInstanceSnapshot>(cancellationToken);
            return Result<RelatedInstanceSnapshot?>.Ok(Normalize(snapshot, reference));
        }
        catch (Exception exception)
        {
            return Result<RelatedInstanceSnapshot?>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"Related instance read to {reference.Domain} failed: {exception.Message}",
                detail: exception.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        if (references.Count == 0)
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]);

        var snapshots = new List<RelatedInstanceSnapshot>(references.Count);

        // One call per (domain, flow, version) group — the endpoint is routed by domain and flow.
        foreach (var group in references.GroupBy(reference =>
                     (reference.Domain, reference.Flow, reference.FlowVersion)))
        {
            var groupResult = await ReadGroupAsync(group.Key, [.. group], cancellationToken);
            if (!groupResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(groupResult.Error);

            snapshots.AddRange(groupResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }

    private async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadGroupAsync(
        (string Domain, string Flow, string? FlowVersion) key,
        IReadOnlyList<RelatedInstanceRef> group,
        CancellationToken cancellationToken)
    {
        var endpointResult = await endpointResolver.GetEndpointAsync(key.Domain, EndpointKind.Url, cancellationToken);
        if (!endpointResult.IsSuccess)
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(endpointResult.Error);

        var relativePath = InstanceUrlTemplates.RelatedDataBatch(key.Domain, key.Flow, ApiVersionPrefix);
        var url = BuildUrl(endpointResult.Value!, relativePath, key.FlowVersion);

        var body = new RelatedDataBatchInput
        {
            InstanceIds = group.Select(reference => reference.InstanceId).ToList()
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync(url, body, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(Error.Failure(
                    WorkflowErrorCodes.TaskExecution,
                    $"Batched related instance read to {key.Domain} returned {(int)response.StatusCode}."));

            var snapshots = await response.Content
                .ReadFromJsonAsync<List<RelatedInstanceSnapshot>>(cancellationToken) ?? [];

            var byId = group.ToDictionary(reference => reference.InstanceId);
            var normalized = snapshots
                .Where(snapshot => byId.ContainsKey(snapshot.InstanceId))
                .Select(snapshot => Normalize(snapshot, byId[snapshot.InstanceId])!)
                .ToList();

            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(normalized);
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"Batched related instance read to {key.Domain} failed: {exception.Message}",
                detail: exception.GetType().Name));
        }
    }

    private static string BuildUrl(string endpoint, string relativePath, string? flowVersion)
    {
        var url = $"{endpoint.TrimEnd('/')}{relativePath}";
        return string.IsNullOrWhiteSpace(flowVersion)
            ? url
            : $"{url}?version={Uri.EscapeDataString(flowVersion)}";
    }

    /// <summary>
    /// The reference is authoritative for domain, flow and version — the remote side does not carry a
    /// domain on the instance aggregate.
    /// </summary>
    private static RelatedInstanceSnapshot? Normalize(
        RelatedInstanceSnapshot? snapshot,
        RelatedInstanceRef reference)
    {
        if (snapshot == null)
            return null;

        return new RelatedInstanceSnapshot
        {
            InstanceId = snapshot.InstanceId == Guid.Empty ? reference.InstanceId : snapshot.InstanceId,
            Key = snapshot.Key,
            Domain = reference.Domain,
            Flow = string.IsNullOrWhiteSpace(snapshot.Flow) ? reference.Flow : snapshot.Flow,
            FlowVersion = snapshot.FlowVersion ?? reference.FlowVersion,
            Status = snapshot.Status,
            CurrentState = snapshot.CurrentState,
            IsCompleted = snapshot.IsCompleted,
            Data = snapshot.Data
        };
    }
}
```

If `BuildUrl`'s shape does not match how the neighbouring remote services compose `endpoint +
relativePath + query`, mirror theirs exactly instead — they are the reference implementation.

`RelatedInstanceSnapshot.Data` is `dynamic?`, so `System.Text.Json` deserializes it into a
`JsonElement`. Verify in Task 11's integration check that scripts can read it; if property access fails,
convert with the existing `ToDynamic()` extension in
`src/BBT.Workflow.Domain/System/Text/Json/ScriptContextExtensions.cs` inside `Normalize`.

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/BBT.Workflow.Infrastructure`

Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.Infrastructure/Gateway/RemoteRelatedInstanceReader.cs
git commit -m "feat(gateway): add remote related instance reader over internal endpoints"
```

---

## Task 11: Local reader, routed reader, and DI

**Files:**
- Create: `src/BBT.Workflow.Infrastructure/Gateway/LocalRelatedInstanceReader.cs`
- Create: `src/BBT.Workflow.Infrastructure/Gateway/RoutedRelatedInstanceReader.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/GatewayServiceCollectionExtensions.cs`
- Test: `test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedRelatedInstanceReaderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedRelatedInstanceReaderTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Related;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Gateway;

public class RoutedRelatedInstanceReaderTests
{
    private static readonly Guid TargetId = Guid.Parse("12121212-1212-1212-1212-121212121212");

    private static RelatedInstanceRef Local() => new(TargetId, "lending", "loan-application", "2.1.0");
    private static RelatedInstanceRef Foreign() => new(TargetId, "compliance", "kyc-flow", "1.0.0");

    private sealed record Harness(
        RoutedRelatedInstanceReader Reader,
        IRelatedInstanceReader LocalReader,
        IRelatedInstanceReader RemoteReader);

    private static Harness CreateHarness()
    {
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.IsDomainMatch("lending").Returns(true);
        runtime.IsDomainMatch("compliance").Returns(false);

        var local = Substitute.For<IRelatedInstanceReader>();
        var remote = Substitute.For<IRelatedInstanceReader>();

        local.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        remote.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        local.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]));
        remote.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]));

        return new Harness(new RoutedRelatedInstanceReader(runtime, local, remote), local, remote);
    }

    [Fact]
    public async Task ReadAsync_ShouldUseLocalReader_WhenDomainMatches()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadAsync(Local(), CancellationToken.None);

        await harness.LocalReader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
        await harness.RemoteReader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task ReadAsync_ShouldUseRemoteReader_WhenDomainDiffers()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadAsync(Foreign(), CancellationToken.None);

        await harness.RemoteReader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
        await harness.LocalReader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task ReadManyAsync_ShouldSplitByDomain()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadManyAsync([Local(), Foreign()], CancellationToken.None);

        await harness.LocalReader.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs => refs.Count == 1 && refs[0].Domain == "lending"),
            Arg.Any<CancellationToken>());
        await harness.RemoteReader.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs => refs.Count == 1 && refs[0].Domain == "compliance"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadManyAsync_ShouldNotCallRemote_WhenEveryRefIsLocal()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadManyAsync([Local(), Local()], CancellationToken.None);

        await harness.RemoteReader.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default);
    }

    [Fact]
    public async Task ReadManyAsync_ShouldFail_WhenOneSideFails()
    {
        var harness = CreateHarness();
        harness.RemoteReader
            .ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(
                Error.Failure("RELATED_READ", "compliance unreachable")));

        var result = await harness.Reader.ReadManyAsync([Local(), Foreign()], CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("compliance unreachable");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~RoutedRelatedInstanceReaderTests"`

Expected: build FAILS — `CS0246: ... 'RoutedRelatedInstanceReader' could not be found`.

- [ ] **Step 3: Create `LocalRelatedInstanceReader.cs`**

```csharp
using BBT.Aether.Results;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads related instances that live in this runtime's domain. Establishes the schema scope with
/// <c>ExecuteWithWorkflowAsync</c> — the same pattern <see cref="LocalInstanceQueryGateway"/> uses —
/// so each read runs in a fresh scope and does not interfere with the caller's unit of work.
/// </summary>
public sealed class LocalRelatedInstanceReader(IServiceScopeFactory serviceScopeFactory) : IRelatedInstanceReader
{
    /// <inheritdoc />
    public Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default) =>
        serviceScopeFactory.ExecuteWithWorkflowAsync(
            reference.Domain,
            reference.Flow,
            reference.FlowVersion,
            async (serviceProvider, ct) =>
            {
                var service = serviceProvider.GetRequiredService<IRelatedInstanceQueryAppService>();
                return await service.ReadAsync(reference, ct);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        if (references.Count == 0)
            return Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(
                (IReadOnlyList<RelatedInstanceSnapshot>)[]));

        return ReadManyCoreAsync(references, cancellationToken);
    }

    private async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyCoreAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<RelatedInstanceSnapshot>(references.Count);

        // One schema scope per flow: different flows resolve to different schemas.
        foreach (var group in references.GroupBy(reference => (reference.Flow, reference.FlowVersion)))
        {
            var groupRefs = group.ToList();
            var groupResult = await serviceScopeFactory.ExecuteWithWorkflowAsync(
                groupRefs[0].Domain,
                group.Key.Flow,
                group.Key.FlowVersion,
                async (serviceProvider, ct) =>
                {
                    var service = serviceProvider.GetRequiredService<IRelatedInstanceQueryAppService>();
                    return await service.ReadManyAsync(groupRefs, ct);
                },
                cancellationToken);

            if (!groupResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(groupResult.Error);

            snapshots.AddRange(groupResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }
}
```

Confirm the exact `ExecuteWithWorkflowAsync` signature before you rely on it:

```bash
grep -rn "ExecuteWithWorkflowAsync" src/BBT.Workflow.Infrastructure --include=*.cs | grep -v Gateway/ | head -3
```

- [ ] **Step 4: Create `RoutedRelatedInstanceReader.cs`**

The house pattern (`RoutedInstanceQueryGateway`) injects the two sides as **concrete** classes, which
makes the router impossible to unit-test with substitutes. This reader deviates on purpose: it takes
`IRelatedInstanceReader` for both sides and uses keyed DI to disambiguate. Keyed-service attributes are
ignored on direct construction, so the test in Step 1 can pass plain substitutes.

First add the key constants to `src/BBT.Workflow.Domain/Scripting/Related/RelatedAccessOptions.cs`:

```csharp
/// <summary>
/// DI keys distinguishing the two <see cref="IRelatedInstanceReader"/> implementations behind the
/// routed reader.
/// </summary>
public static class RelatedReaderKeys
{
    /// <summary>Key of the same-domain reader.</summary>
    public const string Local = "related-instance-reader-local";

    /// <summary>Key of the cross-domain reader.</summary>
    public const string Remote = "related-instance-reader-remote";
}
```

Then create `RoutedRelatedInstanceReader.cs`:

```csharp
using BBT.Aether.Results;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routes related-instance reads: local when the target domain matches this runtime, remote otherwise.
/// Mirrors <see cref="RoutedInstanceQueryGateway"/>, but injects both sides as interfaces (via keyed
/// DI) so the routing decision itself is unit-testable.
/// </summary>
public sealed class RoutedRelatedInstanceReader(
    IRuntimeInfoProvider runtimeInfoProvider,
    [FromKeyedServices(RelatedReaderKeys.Local)] IRelatedInstanceReader local,
    [FromKeyedServices(RelatedReaderKeys.Remote)] IRelatedInstanceReader remote) : IRelatedInstanceReader
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = runtimeInfoProvider;
    private readonly IRelatedInstanceReader _local = local;
    private readonly IRelatedInstanceReader _remote = remote;

    /// <inheritdoc />
    public Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default) =>
        _runtimeInfoProvider.IsDomainMatch(reference.Domain)
            ? _local.ReadAsync(reference, cancellationToken)
            : _remote.ReadAsync(reference, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        var localRefs = references.Where(r => _runtimeInfoProvider.IsDomainMatch(r.Domain)).ToList();
        var remoteRefs = references.Where(r => !_runtimeInfoProvider.IsDomainMatch(r.Domain)).ToList();

        var snapshots = new List<RelatedInstanceSnapshot>(references.Count);

        if (localRefs.Count > 0)
        {
            var localResult = await _local.ReadManyAsync(localRefs, cancellationToken);
            if (!localResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(localResult.Error);

            snapshots.AddRange(localResult.Value!);
        }

        if (remoteRefs.Count > 0)
        {
            var remoteResult = await _remote.ReadManyAsync(remoteRefs, cancellationToken);
            if (!remoteResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(remoteResult.Error);

            snapshots.AddRange(remoteResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }
}
```

- [ ] **Step 5: Register in DI**

In `GatewayServiceCollectionExtensions.AddInstanceGatewayServices`, add to the local/remote block:

```csharp
        services.AddKeyedScoped<IRelatedInstanceReader, LocalRelatedInstanceReader>(RelatedReaderKeys.Local);
        services.AddKeyedScoped<IRelatedInstanceReader, RemoteRelatedInstanceReader>(RelatedReaderKeys.Remote);
```

and to the routed block:

```csharp
        services.AddScoped<IRelatedInstanceReader, RoutedRelatedInstanceReader>();
```

Add `using BBT.Workflow.Scripting.Related;` to the file.

**The registration must live in `AddInstanceGatewayServices`, next to the other routed gateways** — not
in a separate extension method. `ScriptContextBuilder` takes `IRelatedInstanceReader` as an *optional*
dependency, so a host missing the registration would silently give every script a no-op accessor that
reports "no parent, no correlations" with no error. Keeping the registration inside the method that
also registers `IInstanceQueryGateway`/`IInstanceCommandGateway` means it shares their fate: a host
that skips that call has no gateways at all, which fails loudly and immediately for far more than this
feature. Step 6 below adds a test that fails if the line is ever dropped.

`RemoteRelatedInstanceReader` needs an `HttpClient`. Find how `RemoteInstanceQueryAppService` gets one
and copy that registration:

```bash
grep -rn "AddHttpClient<.*RemoteInstance" src/ --include=*.cs
```

- [ ] **Step 6: Add a DI resolution test**

The reader is an *optional* constructor dependency of `ScriptContextBuilder`, so a missing registration
degrades silently instead of failing. Pin the registration with a test rather than a runtime assertion.
Add to `test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedRelatedInstanceReaderTests.cs`:

```csharp
    [Fact]
    public void AddInstanceGatewayServices_ShouldRegisterTheRelatedInstanceReader()
    {
        // The reader is an optional dependency of ScriptContextBuilder: if this registration is ever
        // dropped, every script silently gets a no-op accessor reporting "no parent, no correlations"
        // with no error anywhere. This test is the guard.
        var services = new ServiceCollection();

        services.AddInstanceGatewayServices();

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IRelatedInstanceReader) &&
            descriptor.ImplementationType == typeof(RoutedRelatedInstanceReader));
    }
```

Add `using Microsoft.Extensions.DependencyInjection;` to the test file. If `AddInstanceGatewayServices`
cannot be called against a bare `ServiceCollection` (missing prerequisite registrations), assert on the
descriptors instead of building the provider — the point is that the descriptor exists, not that it
resolves.

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~RoutedRelatedInstanceReaderTests"`

Expected: PASS, 6 tests.

- [ ] **Step 8: Verify the whole solution builds**

Run: `dotnet build vnext.sln`

Expected: `Build succeeded`.

- [ ] **Step 9: Run every test written for this feature**

Run:

```bash
dotnet test vnext.sln --filter "FullyQualifiedName~Related"
```

Expected: PASS — 14 + 16 + 7 + 5 + 8 + 4 + 5 + 6 tests, zero failures.

- [ ] **Step 10: Commit**

```bash
git add src/BBT.Workflow.Infrastructure/Gateway test/BBT.Workflow.Infrastructure.Tests/Gateway/RoutedRelatedInstanceReaderTests.cs
git commit -m "feat(gateway): route related instance reads local or remote by domain"
```

---

## Task 12: Documentation and metadata

**Files:**
- Create: `docs/runtime/script-related-instance-access.md`
- Modify: `docs/README.md`
- Modify: `.claude/rules/vnext-workflow-developer.md`
- Modify: `vnext-meta/features.json`

- [ ] **Step 1: Write `docs/runtime/script-related-instance-access.md`**

```markdown
# Script Related Instance Access

Mapping scripts can read the data of a *related* instance — the parent that started this instance as a
SubFlow/SubProcess, or one of this instance's own correlations — instead of duplicating that data across
the parent/child boundary.

## API

`context.Related` is always available inside any mapping script (`IMapping`, `IConditionMapping`,
`ISubFlowMapping`, `IOutputHandler`, `IEventMapping`).

| Member | Returns | Notes |
|---|---|---|
| `HasParent` | `bool` | Synchronous. No read. |
| `SubKeysAsync()` | `IReadOnlyList<string>` | Distinct sub workflow keys. Loads correlations, reads no data. |
| `ParentAsync()` | `RelatedInstanceView?` | `null` when this instance has no parent. |
| `SubAsync(key)` | `RelatedInstanceView?` | Newest correlation with that sub workflow key. |
| `SubsAsync(key?)` | `IReadOnlyList<RelatedInstanceView>` | All correlations, oldest first. `null` key = every correlation. |

`RelatedInstanceView` fields: `InstanceId`, `Key`, `Domain`, `Flow`, `FlowVersion`, `Status`,
`CurrentState`, `IsCompleted`, `CorrelationCompleted`, `TerminalOutcome`, `SubFlowType`, `Data`.

`IsCompleted` is the **target instance's** status (`C`). `CorrelationCompleted` is whether the
**relationship** is closed, and is `null` for the parent direction. They can disagree: during the
subflow completion window the child is `Completed` while the parent correlation is still open.

## Examples

Read parent data from a subflow's input binding:

```csharp
var parent = await context.Related.ParentAsync();
var limit = GetPropertyValue<decimal>(parent?.Data, "creditLimit", 0m);
```

Gate a view on a child's completion:

```csharp
var kyc = await context.Related.SubAsync("kyc-flow");
return kyc?.IsCompleted == true;
```

Aggregate over repeated subprocesses:

```csharp
var uploads = await context.Related.SubsAsync("doc-upload");
return uploads.Count(u => u.CorrelationCompleted == true) >= 3;
```

## Key resolution

`SubAsync` / `SubsAsync` match against `InstanceCorrelation.SubFlowName` — the **sub workflow key**, not
an alias. When several correlations share a key (a loop, or a subprocess started repeatedly),
`SubAsync` returns the most recently created one; use `SubsAsync` to see them all and choose explicitly.

Both active and completed correlations are visible.

## Scope

Exactly one hop. The parent's parent, and a child's children, are not reachable — by design.

## Security: reads are unfiltered

Related-instance reads use the engine's own system identity. They deliberately skip:

- the query-role check (`QueryAccessDenied` is never raised),
- `x-roles` field-level filtering,
- extensions,
- the data-function response cache.

This keeps script decisions deterministic across users and makes the API work in scheduled, automatic,
event and background-job contexts, where no caller identity exists.

> **Warning.** `x-roles` protection does not follow a copy. If an output mapping writes a related
> instance's restricted field into *this* instance's data, that field becomes readable by any client
> entitled to read this instance. Only copy fields you intend to expose.

Every cross-domain read is logged (`RelatedInstanceCrossDomainRead`, event id 20432).

## Errors

| Situation | Behaviour |
|---|---|
| No parent, no matching correlation, target instance gone | `null` (or an empty list), Debug log |
| Read failure — cross-domain HTTP error, DB error, discovery failure | throws `RelatedInstanceAccessException`; the error boundary handles it |
| More than `MaxResolutionsPerContext` distinct related instances in one script | throws `RelatedInstanceAccessException` |

Absence is data; failure is a fault. A silent `null` on a read failure would be indistinguishable from
"there is no parent" and would quietly produce a wrong business decision.

## Performance

Nothing is pre-fetched. Resolved instances are memoized for the lifetime of the `ScriptContext`, so
repeated reads across `onExecute` / `onExit` / `onEntry` and view conditions in one transition cost one
read. `SubsAsync` batches — it never issues one call per correlation. Parallel task branches share the
coordinator's memo.

Same-domain reads never leave the process: `RoutedRelatedInstanceReader` dispatches locally when
`IRuntimeInfoProvider.IsDomainMatch` holds, and only crosses the network for foreign domains.

## Configuration

```json
{
  "Workflow": {
    "Scripting": {
      "RelatedAccess": {
        "MaxResolutionsPerContext": 10
      }
    }
  }
}
```

## Internal endpoints

Cross-domain reads go over two internal-only routes, excluded from the public Swagger group:

- `GET  api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/internal/related-data`
- `POST api/v{version}/{domain}/workflows/{workflow}/internal/related-data/batch`
```

- [ ] **Step 2: Add the navigation entry**

In `docs/README.md`, add to the Runtime group (match the surrounding link format exactly):

```markdown
- [Script Related Instance Access](runtime/script-related-instance-access.md) — reading parent/correlation data from mapping scripts
```

- [ ] **Step 3: Add the quick-reference block to the rules file**

Append to `.claude/rules/vnext-workflow-developer.md`, after the "Instance Data" section:

```markdown
## Related Instance Access (scripts)

- `context.Related` — one hop only: `ParentAsync()` (up, from `parent.*` ExtraProperties) and
  `SubAsync(key)` / `SubsAsync(key?)` / `SubKeysAsync()` (down, from correlations incl. completed).
- Key = `InstanceCorrelation.SubFlowName` (sub workflow key). `SubAsync` = newest by `CreatedAt`.
- `IsCompleted` = target instance status `C`; `CorrelationCompleted` = relationship closed (null for parent).
  They disagree during the subflow completion window.
- Reads are **system-identity and unfiltered**: no query-role check, no `x-roles` filter, no extensions.
  Copying a related field into instance data bypasses `x-roles` — document it where you do it.
- Absence → `null`. Read failure or resolution-cap breach → `RelatedInstanceAccessException`.
- Same domain → in-process (`RoutedRelatedInstanceReader`); cross-domain → internal `related-data`
  endpoints. Memoized per `ScriptContext`; cap `Workflow:Scripting:RelatedAccess:MaxResolutionsPerContext`
  (default 10). Full guide: `docs/runtime/script-related-instance-access.md`.
```

- [ ] **Step 4: Update `vnext-meta/features.json`**

Read the file first to match its schema exactly:

```bash
head -40 vnext-meta/features.json
```

Add a capability entry for related instance access and the two internal endpoints to the endpoint list,
using the existing entries' field names and the current runtime version from `common.props`:

```bash
grep -n "<Version>" common.props
```

- [ ] **Step 5: Validate the metadata**

Run the meta validator skill: invoke `Skill` with `vnext-meta-validator`.

Expected: no schema or version-consistency errors.

- [ ] **Step 6: Commit**

```bash
git add docs/runtime/script-related-instance-access.md docs/README.md .claude/rules/vnext-workflow-developer.md vnext-meta/features.json
git commit -m "docs(scripting): document related instance access"
```

---

## Final verification

- [ ] **Step 1: Full build**

Run: `dotnet build vnext.sln`

Expected: `Build succeeded`, zero warnings introduced by this branch.

- [ ] **Step 2: All feature tests**

Run: `dotnet test vnext.sln --filter "FullyQualifiedName~Related"`

Expected: 65 tests, all passing.

- [ ] **Step 3: No regression against the recorded baseline**

Run:

```bash
dotnet test vnext.sln 2>&1 | tail -30
```

Compare the failure count with `/tmp/test-baseline.txt` from the Baseline section. It must not increase.
If it did, the newly failing tests are yours to fix — do not proceed until the counts match.

- [ ] **Step 4: Confirm the internal endpoints are hidden**

Run:

```bash
grep -n -B2 "internal/related-data" orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Instances/InstanceController.cs
```

Expected: both routes carry `[ApiExplorerSettings(IgnoreApi = true)]`.

- [ ] **Step 5: Hand off**

Use `superpowers:finishing-a-development-branch` to choose merge / PR / cleanup.
