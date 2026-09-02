# Full Span Visibility (Function / Extension / Task Invocation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five instrumented-code gaps found in trace analysis (2026-09-01) so that every phase of function, extension, start and transition execution is attributable to a named span — and fix the explicit-parent overload that severs baggage in existing span helpers.

**Architecture:** All changes are additive `System.Diagnostics.Activity` spans created through the existing per-area ActivityHelper pattern (one static helper per ActivitySource, Business-category tags, registered via `Telemetry:Tracing:AdditionalSources`). Two new sources (`BBT.Workflow.Functions`, `BBT.Workflow.Extensions`); the rest reuse existing sources. No behavior change, no API change, no new dependency.

**Tech Stack:** .NET 10, System.Diagnostics.ActivitySource, xUnit + NSubstitute + Shouldly, OpenTelemetry export via Aether.

**Spec:** Embedded — see "Gap inventory" below. Conversation analysis of trace `036088b9b022a40a938afdc981b6567f` (local Elastic) and `d04a7f5c24ff6041ab44555df0e3d2e6` (intprod).

## Gap inventory (the spec)

| # | Gap | Evidence | New span(s) |
|---|-----|----------|-------------|
| G1 | `Task.Execute.{key}` head: factory resolve + journal persist unspanned (`TaskExecutionEngine.ExecuteCoreAsync`) | 47.8 ms gap before first `Cache.Get sys-tasks` | `Task.Resolve`, `Task.Journal.Create`, `Task.Journal.Complete` |
| G2 | Execution txn head: transport binding + handler work before `Invoke.{type}/{key}` | 57.8 ms gap (p50 baseline 0.44 ms) | `Execution.HandleInvoke` |
| G3 | `Invoke.{type}/{key}` → outbound call: binding deserialize, client create, header/URL/body build | 27.5 ms gap | `Invoke.Prepare` (ALL invokers — user decision) |
| G4 | Function path (`FunctionAppService.ExecuteFunctionAsync`): zero phase spans; `IOutputHandler` .csx runs unspanned | unattributed root-txn tail | `Function.Execute/{key}`, `Function.Authorize`, `Function.ValidateRequest`, `Function.BuildResponse`, `Script.Execute` (scriptKind=`functionOutput`) |
| G5 | Extension path (`InstanceExtensionService`): zero spans; ref fetch invisible | orphan 27 ms `Cache.Get sys-extensions` | `Extension.Process`, `Extension.Resolve`; `vnext.task.trigger` tag on `Task.Execute` |
| G0 | Existing helpers use explicit-parent `StartActivity(name, kind, Activity.Current?.Context ?? default)` → `Activity.Parent` stays null → **baggage severed** for everything under those spans | documented trap (read-path-trace-gap memory; `InstanceReadActivityHelper` doc comment) | overload fix (user decision: in scope) |

## Global Constraints

- **All new spans are Business category, always-on** (user decision) — tag `vnext.span.category = business` via `TelemetryConstants.TagNames.SpanCategory` / `SpanCategories.Business`.
- **Implicit-parent overload ONLY**: `ActivitySource.StartActivity(name, ActivityKind.Internal)` — never pass `Activity.Current?.Context`. Exception list (deliberate explicit parents, DO NOT touch): `FlatLaneActivity`, `BackgroundJobActivityHelper`, `TaskInvokeHandler.RestoreActivityFromBodyIfDetached`.
- **Never read a helper's static `ActivitySource` field inside an `ActivityListener.ShouldListenTo` predicate** — re-enters the type initializer and poisons the type. Use a literal string or `public const string SourceName` in tests.
- Test classes that install an `ActivityListener` must carry `[Collection("TracingDetailLevel")]` and dispose the listener + reset `Activity.Current = null` in `Dispose()` (pattern: `test/BBT.Workflow.Application.Tests/Tasks/Executors/TriggerLocalScopeTests.cs`).
- **Existing transaction labels must stay on transactions** — Elastic prod queries read `labels.vnext_task_key` etc. from the Execution TRANSACTION document; do not let new child spans steal those tags (see Task 4 ordering note).
- Additive only — no renames of existing span names, tags, or sources (repo no-breaking-change policy).
- Build: `dotnet build src/BBT.Workflow.Application execution/BBT.Workflow.Execution.HttpApi.Host`. Tests: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~<TestClass>"`.
- Note: master carries ~191 pre-existing test failures (AmbientServiceProvider parallel-collection leakage). Judge success by the targeted `--filter` runs, not the full suite.

---

### Task 1: Baggage fix — switch span helpers to the implicit-parent overload

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionActivityHelper.cs` (2 sites: `StartLocalTriggerActivity` ~line 68, `StartActivity` ~line 109)
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs` (3 sites: `StartCompileActivity` ~37, `StartExecuteActivity` ~62, `StartResolveHelpersActivity` ~76)
- Modify: `src/BBT.Workflow.Execution/Services/InvokerActivityHelper.cs` (2 sites: `StartInvokeActivity` ~43, `StartCacheAsideActivity` ~63)
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/PipelineStepActivityHelper.cs` (2 sites: ~25, ~77)
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubFlowActivityHelper.cs` (1 site: ~30)
- Modify: `src/BBT.Workflow.Application/Caching/CacheActivityHelper.cs` (1 site: ~82)
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/SpanHelperBaggageTests.cs` (create)

**Interfaces:**
- Consumes: existing helper method signatures (unchanged).
- Produces: no signature change — behavior change only: spans started by these helpers now have `Activity.Parent != null`, so baggage flows to their children. Later tasks assume this.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the implicit-parent contract for span helpers: a span started under an ambient activity
/// must keep the Activity CHAIN intact (Parent != null) so baggage is inherited. The explicit
/// parentContext overload sets ParentSpanId but leaves Parent null, silently severing baggage —
/// the defect documented in InstanceReadActivityHelper and the read-path-trace-gap work.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class SpanHelperBaggageTests : IDisposable
{
    // Literals, NOT Helper.ActivitySource.Name — reading the static field inside
    // ShouldListenTo re-enters the helper's type initializer (process-poisoning NRE).
    private static readonly string[] Sources =
    [
        "BBT.Workflow.Tasks", "BBT.Workflow.Scripting",
        "BBT.Workflow.Execution.Invokers", "BBT.Workflow.Pipeline",
        "BBT.Workflow.SubFlow", "BBT.Workflow.Cache"
    ];

    private readonly ActivityListener _listener;

    public SpanHelperBaggageTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => Array.IndexOf(Sources, s.Name) >= 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    private static Activity StartAmbientWithBaggage()
    {
        var ambient = new Activity("ambient-root");
        ambient.AddBaggage("vnext.test.baggage", "carried");
        ambient.Start();
        return ambient;
    }

    private static void AssertInheritsBaggage(Activity? span)
    {
        span.ShouldNotBeNull();
        span.Parent.ShouldNotBeNull("explicit-parent overload severs the Activity chain");
        span.GetBaggageItem("vnext.test.baggage").ShouldBe("carried");
        span.Dispose();
    }

    [Fact]
    public void TaskExecutionHelper_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(TaskExecutionActivityHelper.StartActivity("Task.PrepareInput", "k", "Http"));
    }

    [Fact]
    public void Script_execute_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(ScriptActivityHelper.StartExecuteActivity("lockKey"));
    }

    [Fact]
    public void Invoker_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(BBT.Workflow.Execution.Services.InvokerActivityHelper.StartInvokeActivity("http", "k"));
    }

    [Fact]
    public void SubFlow_span_inherits_baggage()
    {
        using var ambient = StartAmbientWithBaggage();
        AssertInheritsBaggage(BBT.Workflow.SubFlow.Services.SubFlowActivityHelper.StartActivity("SubFlow.Test"));
    }
}
```

Adjust the `SubFlowActivityHelper` namespace/using to the actual one in `src/BBT.Workflow.Application/SubFlow/Services/SubFlowActivityHelper.cs` (read the file's `namespace` line first). If `CacheActivityHelper.StartActivity`'s signature needs more arguments, add a fifth `[Fact]` matching its real signature the same way.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SpanHelperBaggageTests"`
Expected: FAIL — `span.Parent` is null (baggage assertion may also fail).

- [ ] **Step 3: Fix each helper**

In every listed site, the pattern is the same. Before:

```csharp
var activity = ActivitySource.StartActivity(
    operationName,
    ActivityKind.Internal,
    parentContext);   // or: Activity.Current?.Context ?? default
```

After (delete the parent argument AND any now-unused `parentContext` local):

```csharp
var activity = ActivitySource.StartActivity(
    operationName,
    ActivityKind.Internal);
```

Do NOT touch: `FlatLaneActivity`, `BackgroundJobActivityHelper`, `TaskInvokeHandler` (their explicit parents are deliberate). Verify completeness:

```bash
grep -rn "Activity.Current?.Context ?? default" src/ execution/ --include="*.cs"
```

Expected remaining hits: only the three deliberate files above (plus any test code).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SpanHelperBaggageTests"`
Expected: PASS.

- [ ] **Step 5: Run neighboring span tests for regressions**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TriggerLocalScopeTests|FullyQualifiedName~FanOutTaskExecutorObservabilityTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/ test/BBT.Workflow.Application.Tests/Telemetry/
git commit -m "fix(telemetry): use implicit-parent StartActivity in span helpers so baggage survives"
```

---

### Task 2: G1a — `Task.Resolve` span in both task factories

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionActivityHelper.cs` (add const)
- Modify: `src/BBT.Workflow.Application/Tasks/Factory/TaskFactory.cs:23-32`
- Modify: `src/BBT.Workflow.Application/Tasks/Factory/PooledTaskFactory.cs:29-38`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Factory/TaskFactoryResolveSpanTests.cs` (create)

**Interfaces:**
- Consumes: `TaskExecutionActivityHelper.StartActivity(string operationName, string? taskKey, string? taskType)` (existing, from Task 1's fixed version).
- Produces: const `TaskExecutionActivityHelper.OperationResolve = "Task.Resolve"`. Span emitted on source `BBT.Workflow.Tasks` for every `CreateExecutionTaskAsync` call (engine, FanOut, CacheAside — all call sites covered because the span lives inside the factories).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Factory;

/// <summary>
/// Pins the Task.Resolve span: component-ref resolution + clone inside the task factory was the
/// unspanned head of Task.Execute (47.8 ms unattributed in trace 036088b9…). One span per
/// CreateExecutionTaskAsync call, emitted from INSIDE the factory so engine, FanOut and
/// CacheAside call sites are all covered.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class TaskFactoryResolveSpanTests : IDisposable
{
    private const string TaskSourceName = "BBT.Workflow.Tasks"; // literal — see ShouldListenTo trap

    private readonly ActivityListener _listener;
    private readonly List<Activity> _started = [];

    public TaskFactoryResolveSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TaskSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (_started) _started.Add(a); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    [Fact]
    public async Task CreateExecutionTaskAsync_emits_TaskResolve_span()
    {
        // Mirror the arrange style of TaskExecutionEngineTests: componentCacheStore substitute
        // returning a cached task; use whichever concrete WorkflowTask subtype that fixture uses.
        var cacheStore = Substitute.For<IComponentCacheStore>();
        var reference = Substitute.For<IReference>();
        reference.Key.Returns("my-task");
        var cached = TestTaskContexts.CreateHttpTask("my-task"); // reuse existing test builder; adjust name to actual helper
        cacheStore.GetTaskAsync(reference, Arg.Any<CancellationToken>())
            .Returns(BBT.Aether.Results.Result<WorkflowTask>.Ok(cached));

        var factory = new TaskFactory(cacheStore, NullLogger<TaskFactory>.Instance);

        var result = await factory.CreateExecutionTaskAsync(reference);

        result.IsSuccess.ShouldBeTrue();
        Activity? resolve;
        lock (_started) resolve = _started.FirstOrDefault(a => a.OperationName == "Task.Resolve");
        resolve.ShouldNotBeNull();
        resolve.GetTagItem("vnext.task.key").ShouldBe("my-task");
    }
}
```

Before finalizing: open `test/BBT.Workflow.Application.Tests/Tasks/TestTaskContexts.cs` and `TaskExecutionEngineTests.cs` and copy their exact task-construction helper and `TaskFactory` constructor argument list (logger type parameter, cache store interface namespace). The test must compile against real signatures, not the sketch above.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskFactoryResolveSpanTests"`
Expected: FAIL — no `Task.Resolve` activity started.

- [ ] **Step 3: Add the const and wrap both factories**

In `TaskExecutionActivityHelper` next to the other operation consts:

```csharp
    /// <summary>
    /// Operation name for component-ref resolution + clone inside the task factory — the
    /// previously unspanned head of <c>Task.Execute.{key}</c>.
    /// </summary>
    public const string OperationResolve = "Task.Resolve";
```

In BOTH `TaskFactory.CreateExecutionTaskAsync` and `PooledTaskFactory.CreateExecutionTaskAsync` (bodies are currently identical):

```csharp
    public async Task<Result<WorkflowTask>> CreateExecutionTaskAsync(
        IReference taskReference,
        CancellationToken cancellationToken = default)
    {
        // Task.Resolve lives INSIDE the factory (not at the engine call site) so FanOut and
        // CacheAside resolutions are covered too. Always-on Business span — this was the
        // unattributed head of Task.Execute.
        using var resolveActivity = TaskExecutionActivityHelper.StartActivity(
            TaskExecutionActivityHelper.OperationResolve, taskReference.Key);

        return await componentCacheStore.GetTaskAsync(taskReference, cancellationToken)
            .Then(CreateFromCached)
            .OnFailure(error =>
            {
                TaskExecutionActivityHelper.SetError(Activity.Current, error.Message, "TaskFactoryError");
                logger.LogError(
                    "Failed to create execution task for reference {TaskReference}: {ErrorCode}",
                    taskReference.ToString(), error.Code);
            });
    }
```

Add `using BBT.Workflow.Tasks.Coordinator;` if the namespace differs. Check `SetError`'s exact signature in the helper before using it (it takes `(Activity?, string?, string?, int?)`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskFactoryResolveSpanTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Tasks/ test/BBT.Workflow.Application.Tests/Tasks/Factory/
git commit -m "feat(telemetry): Task.Resolve span covers factory resolution head of Task.Execute"
```

---

### Task 3: G1b — `Task.Journal.Create` / `Task.Journal.Complete` spans + `vnext.task.trigger` tag

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` (add `TaskTrigger` tag const near `TaskType`, line ~42)
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionActivityHelper.cs` (add 2 consts)
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionEngine.cs` (`ExecuteAsync` ~line 99 for the tag; `ExecuteCoreAsync` ~lines 598-601 and the `PersistCompletionAsync` call ~line 683)
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskExecutionEngineTests.cs` (extend)

**Interfaces:**
- Consumes: `TaskExecutionActivityHelper.StartActivity` (Task 1), existing `TaskExecutionEngineTests` fixture (`CreateEngine()`, `ArrangeSuccessfulExecution`, `UsePersistenceStrategy`).
- Produces: consts `OperationJournalCreate = "Task.Journal.Create"`, `OperationJournalComplete = "Task.Journal.Complete"`; tag `TelemetryConstants.TagNames.TaskTrigger = "vnext.task.trigger"` stamped on the `Task.Execute.{key}` span. Task 7 documents these names.

- [ ] **Step 1: Write the failing test (extend TaskExecutionEngineTests)**

Add to the existing file — reuse its fixture; it already builds a fully-substituted engine. Install a listener the same way as `TriggerLocalScopeTests` (literal source name `"BBT.Workflow.Tasks"`), collecting started activities into a list. If `TaskExecutionEngineTests` lacks a listener, add one in the constructor + `Dispose`, and add `[Collection("TracingDetailLevel")]` if not present.

```csharp
    [Fact]
    public async Task Successful_execution_emits_journal_spans_and_trigger_tag()
    {
        var factoryTask = /* same construction ArrangeSuccessfulExecution uses */;
        ArrangeSuccessfulExecution(factoryTask);

        await CreateEngine().ExecuteAsync(
            /* same argument list as the existing success-path test,
               with taskTrigger: TaskTrigger.OnExecute (or whatever that test passes) */);

        var names = StartedActivities.Select(a => a.OperationName).ToList();
        names.ShouldContain("Task.Journal.Create");
        names.ShouldContain("Task.Journal.Complete");

        // trigger tag lands on whatever activity was ambient during ExecuteAsync; assert on
        // the recorded activity that carries the vnext.task.key tag for this task.
        var executeSpan = StartedActivities.FirstOrDefault(
            a => Equals(a.GetTagItem("vnext.task.trigger"), "OnExecute"));
        executeSpan.ShouldNotBeNull();
    }
```

Copy the argument list verbatim from the nearest passing success-path test in the same file (line ~251 `await CreateEngine().ExecuteAsync(...)`). Note: `Task.Execute.{key}` itself comes from Aether's `[Trace]` aspect (source not in this listener) — that's why the trigger-tag assertion must be tolerant: if no ambient activity exists in the unit test, start one manually around the `ExecuteAsync` call (`using var ambient = new Activity("test-root").Start();`) and assert the tag on `ambient`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutionEngineTests"`
Expected: the new test FAILS (no journal spans); pre-existing tests still PASS.

- [ ] **Step 3: Implement**

`TelemetryConstants.TagNames` (next to `TaskType`):

```csharp
        /// <summary>Task trigger origin (OnExecute/OnEntry/OnExit/Extension/…): <c>vnext.task.trigger</c>.</summary>
        public const string TaskTrigger = "vnext.task.trigger";
```

`TaskExecutionActivityHelper` consts:

```csharp
    /// <summary>Operation name for the journal-row creation/probe persist.</summary>
    public const string OperationJournalCreate = "Task.Journal.Create";

    /// <summary>Operation name for the journal-row completion persist.</summary>
    public const string OperationJournalComplete = "Task.Journal.Complete";
```

`TaskExecutionEngine.ExecuteAsync` — inside the existing `if (activity != null)` block (~line 102):

```csharp
            activity.SetTag(TelemetryConstants.TagNames.TaskTrigger, taskTrigger.ToString());
```

`TaskExecutionEngine.ExecuteCoreAsync` — wrap the two persist calls:

```csharp
        // 5. Persist creation
        using (TaskExecutionActivityHelper.StartActivity(
                   TaskExecutionActivityHelper.OperationJournalCreate, task.Key, taskTypeStr))
        {
            instanceTask = await PersistCreationAsync(
                persistenceStrategy, instanceTask, taskTrigger, onExecuteTask.Order,
                options.SkipJournalProbe, cancellationToken);
        }
```

```csharp
        // Persist completion
        using (TaskExecutionActivityHelper.StartActivity(
                   TaskExecutionActivityHelper.OperationJournalComplete, task.Key, taskTypeStr))
        {
            await PersistCompletionAsync(persistenceStrategy, instanceTask, cancellationToken);
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskExecutionEngineTests"`
Expected: PASS (all, including pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs src/BBT.Workflow.Application/Tasks/Coordinator/ test/BBT.Workflow.Application.Tests/Tasks/Coordinator/
git commit -m "feat(telemetry): Task.Journal.* spans + vnext.task.trigger tag on Task.Execute"
```

---

### Task 4: G2 — `Execution.HandleInvoke` span in TaskInvokeHandler

**Files:**
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/Services/TaskInvokeHandler.cs:44-115`

No unit test: the Execution host project is referenced by no test project today, and adding that reference is out of this plan's scope. Coverage comes from Task 8's manual trace verification (acceptance query included there). State this in the commit message.

**Interfaces:**
- Consumes: existing private `static readonly ActivitySource ActivitySource = new("BBT.Workflow.Execution")` in the same class (already export-registered via the `BBT.Workflow.Execution*` wildcard in the Execution host's `AdditionalSources`).
- Produces: span `Execution.HandleInvoke` wrapping tag work + registry invocation + response mapping. Remaining txn head = transport binding + middleware, measurable by subtraction (`txn.start → Execution.HandleInvoke.start`).

- [ ] **Step 1: Implement (ordering is the critical part)**

In `HandleAsync`, immediately after `using var restoredActivity = RestoreActivityFromBodyIfDetached(traceContext);` and the existing `var activity = Activity.Current;` line, KEEP the transaction reference and start the new span AFTER capturing it:

```csharp
        using var restoredActivity = RestoreActivityFromBodyIfDetached(traceContext);

        // The ASP.NET/gRPC transaction — captured BEFORE the child span below so every
        // SetTag/SetBaggage keeps landing on the TRANSACTION document. Elastic prod queries
        // filter execution transactions by labels.vnext_task_key; letting the child span become
        // Activity.Current first would silently move those labels off the transaction.
        var activity = Activity.Current;

        // Everything from here (tagging, registry resolution, invocation, response mapping) is
        // inside one always-on span; the remaining head of the transaction is then pure
        // transport work (model binding / protobuf parse / middleware), measurable by
        // subtraction. Closes the 57.8 ms unattributed head found in trace 036088b9….
        using var handleActivity = ActivitySource.StartActivity(
            "Execution.HandleInvoke", ActivityKind.Internal);
        handleActivity?.SetTag(TelemetryConstants.TagNames.TaskKey, envelope.TaskKey);
        handleActivity?.SetTag(TelemetryConstants.TagNames.TaskType, envelope.TaskType);
        handleActivity?.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Execution);
        handleActivity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
```

The rest of the method body is unchanged (all existing `activity?.SetTag(...)` lines keep targeting the captured transaction). NOTE: existing `SetBaggage` calls go through `activity` (the txn) — baggage set on the txn is still inherited by `handleActivity`'s children because `handleActivity` was started before? **No** — baggage added to the txn AFTER `handleActivity` started does NOT retroactively appear on `handleActivity`, but children of `handleActivity` resolve `GetBaggageItem` through the parent CHAIN at read time, so txn baggage IS visible to them. No change needed; do not move the baggage calls.

- [ ] **Step 2: Build**

Run: `dotnet build execution/BBT.Workflow.Execution.HttpApi.Host`
Expected: success, no warnings introduced.

- [ ] **Step 3: Commit**

```bash
git add execution/BBT.Workflow.Execution.HttpApi.Host/Services/TaskInvokeHandler.cs
git commit -m "feat(telemetry): Execution.HandleInvoke span isolates transport head of invoke txn

No unit test: host project has no test-project reference; covered by the
manual trace verification task in the full-span-visibility plan."
```

---

### Task 5: G3 — `Invoke.Prepare` span in all invokers

**Files:**
- Modify: `src/BBT.Workflow.Execution/Services/InvokerActivityHelper.cs` (new method)
- Modify (14 invokers, anchors below): everything under `src/BBT.Workflow.Execution/Invokers/` except `CacheAsideTaskInvoker.cs` (it does no outbound prep — it wraps the registry, which re-enters `Invoke.*`; skipping it is a deliberate exception to "all invokers", note it in the commit)
- Test: `test/BBT.Workflow.Application.Tests/Tasks/Invokers/InvokePrepareSpanTests.cs` (create)

**Interfaces:**
- Consumes: `InvokerActivityHelper` source `BBT.Workflow.Execution.Invokers` (registered by `BBT.Workflow.Execution*` wildcard on the Execution host; Task 7 adds it to the other hosts).
- Produces: `InvokerActivityHelper.StartPrepareActivity(string taskType, string taskKey)` returning `Activity?` named `Invoke.Prepare` with `vnext.task.key`/`vnext.task.type` tags. Child of the ambient `Invoke.{type}/{key}`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using System.Net;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invokers;

/// <summary>
/// Pins Invoke.Prepare: the gap between Invoke.{type}/{key} and the outbound client span
/// (binding deserialize + client create + header/URL/body build) gets its own always-on span.
/// HttpTaskInvoker is the representative; the same helper call is applied to every invoker.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class InvokePrepareSpanTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Execution.Invokers"; // literal — trap

    private readonly ActivityListener _listener;
    private readonly List<Activity> _started = [];

    public InvokePrepareSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (_started) _started.Add(a); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() { _listener.Dispose(); Activity.Current = null; }

    [Fact]
    public async Task HttpTaskInvoker_emits_InvokePrepare_before_send()
    {
        // Reuse the arrange helpers from HttpTaskInvokerContentTypeTests (stub HttpMessageHandler
        // + IHttpClientFactory substitute + a minimal HttpTaskBinding JsonElement). Copy that
        // file's builder verbatim — it already constructs the invoker with all dependencies.
        var invoker = /* construct exactly as HttpTaskInvokerContentTypeTests does */;
        var (taskKey, binding) = /* same fixture's binding for a simple GET */;

        var result = await invoker.InvokeAsync(taskKey, binding, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        lock (_started)
        {
            _started.ShouldContain(a => a.OperationName == "Invoke.Prepare");
            var prep = _started.First(a => a.OperationName == "Invoke.Prepare");
            prep.GetTagItem("vnext.task.key").ShouldBe(taskKey);
        }
    }
}
```

Read `HttpTaskInvokerContentTypeTests.cs` first and lift its construction/fixture code exactly — the sketch's two `/* … */` holes must be real code before Step 2.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~InvokePrepareSpanTests"`
Expected: FAIL — no `Invoke.Prepare` activity.

- [ ] **Step 3: Add the helper method**

In `InvokerActivityHelper`:

```csharp
    /// <summary>
    /// Starts the span covering everything an invoker does BEFORE its outbound call — binding
    /// deserialization, client construction, header/URL/body preparation. Dispose it immediately
    /// before the I/O call so the trace separates "our prep" from "their latency". Always-on:
    /// this gap measured 27 ms in the trace that motivated it, with nothing to attribute it to.
    /// </summary>
    public static Activity? StartPrepareActivity(string taskType, string taskKey)
    {
        var activity = ActivitySource.StartActivity("Invoke.Prepare", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TagTaskKey, taskKey);
            activity.SetTag(TagTaskType, taskType);
        }
        return activity;
    }
```

- [ ] **Step 4: Apply to each invoker**

The uniform pattern — declare the span at the top of the method that performs preparation, dispose it just before the outbound call:

```csharp
        var prepareActivity = InvokerActivityHelper.StartPrepareActivity("<tasktype>", taskKey);
        try
        {
            // …existing deserialize / client create / request build code, unchanged…
        }
        finally
        {
            prepareActivity?.Dispose();
        }
```

Concretely: dispose (`prepareActivity?.Dispose();`) on the line ABOVE each outbound call, and remove the `finally` form where a linear flow allows a plain early dispose. Anchors (current line numbers; re-locate by the call text, not the number):

| File | Prep starts at | Dispose immediately before |
|---|---|---|
| `HttpTaskInvoker.cs` | top of `ExecuteAsync` (line ~49) | `httpClient.SendAsync(request, …)` line ~97 |
| `SoapTaskInvoker.cs` | top of its execute method | `httpClient.SendAsync(request, …)` line ~98 |
| `DaprServiceTaskInvoker.cs` | top of execute method | the `await daprClient.InvokeMethodAsync`-family call that consumes the request built at line ~58 |
| `DaprHttpEndpointTaskInvoker.cs` | top of execute method | same — the await consuming the request from line ~58 |
| `DaprBindingTaskInvoker.cs` | top of execute method | `daprClient.InvokeBindingAsync(` line ~88 |
| `DaprPubSubTaskInvoker.cs` | top of execute method | `_daprClient.PublishEventAsync(` line ~84 |
| `DaprConversationTaskInvoker.cs` | top of execute method | `conversationClient.ConverseAsync(` line ~92 |
| `StateStoreTaskInvoker.cs` | top of execute method | the first Dapr state operation (`GetStateAsync`/`SaveStateAsync`/similar — locate by reading the file) |
| `GetInstanceRemoteInvoker.cs` | top of the method containing line ~132 | `httpClient.SendAsync(request, …)` ~132; if the Dapr branch (~236) is a separate code path, wrap its prep the same way before its await |
| `GetInstanceDataRemoteInvoker.cs` | same pattern | `SendAsync` ~132 / Dapr branch ~226 |
| `GetInstancesRemoteInvoker.cs` | same pattern | `SendAsync` ~132 / Dapr branch ~240 |
| `StartTriggerRemoteInvoker.cs` | same pattern | `SendAsync` ~133 / Dapr branch ~237 |
| `DirectTriggerRemoteInvoker.cs` | same pattern | `SendAsync` ~194 / Dapr branch ~304 |
| `SubProcessRemoteInvoker.cs` | same pattern | `SendAsync` ~135 / Dapr branch ~250 |

Each invoker's task-type string: use the invoker's own `TaskType` property value (every `ITaskInvoker` exposes it — pass `TaskType` rather than a fresh literal).

- [ ] **Step 5: Run test + existing invoker tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~InvokePrepareSpanTests|FullyQualifiedName~HttpTaskInvoker|FullyQualifiedName~StateStoreTaskInvokerTests|FullyQualifiedName~DaprConversationTaskInvokerTests|FullyQualifiedName~CacheAsideTaskInvokerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Execution/ test/BBT.Workflow.Application.Tests/Tasks/Invokers/
git commit -m "feat(telemetry): Invoke.Prepare span in all task invokers (CacheAside excepted: no outbound prep)"
```

---

### Task 6: G4 — Function path spans

**Files:**
- Create: `src/BBT.Workflow.Application/Functions/FunctionActivityHelper.cs`
- Modify: `src/BBT.Workflow.Application/Functions/FunctionAppService.cs` (`ExecuteFunctionAsync` ~135, `BuildResponseAsync` ~412)
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs` (doc comment only — `StartExecuteActivity`'s call-site list gains `functionOutput`)
- Test: `test/BBT.Workflow.Application.Tests/Functions/FunctionActivityHelperTests.cs` (create)

**Interfaces:**
- Consumes: `ScriptActivityHelper.StartExecuteActivity(string scriptKind)` (existing).
- Produces: `FunctionActivityHelper` with `public const string SourceName = "BBT.Workflow.Functions"` and methods `StartExecute(string functionKey)` → span `Function.Execute/{key}`, `StartPhase(string operationName)` → spans `Function.Authorize` / `Function.ValidateRequest` / `Function.BuildResponse` (op-name consts on the helper). Task 7 registers the source in appsettings.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using BBT.Workflow.Functions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

[Collection("TracingDetailLevel")]
public sealed class FunctionActivityHelperTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Functions"; // literal — ShouldListenTo trap

    private readonly ActivityListener _listener;

    public FunctionActivityHelperTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() { _listener.Dispose(); Activity.Current = null; }

    [Fact]
    public void Execute_span_carries_key_layer_and_category()
    {
        using var span = FunctionActivityHelper.StartExecute("my-fn");
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Function.Execute/my-fn");
        span.GetTagItem("vnext.span.category").ShouldBe("business");
        span.GetTagItem("vnext.layer").ShouldBe("orchestration");
    }

    [Fact]
    public void Phase_span_inherits_baggage_from_ambient()
    {
        using var ambient = new Activity("root");
        ambient.AddBaggage("k", "v");
        ambient.Start();

        using var span = FunctionActivityHelper.StartPhase(FunctionActivityHelper.OperationValidateRequest);
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Function.ValidateRequest");
        span.GetBaggageItem("k").ShouldBe("v");
    }
}
```

Check the literal values of `TelemetryConstants.SpanCategories.Business` and `TelemetryConstants.Layers.Orchestration` (they may be `"business"`/`"orchestration"` or capitalized) and assert those exact values.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FunctionActivityHelperTests"`
Expected: FAIL — `FunctionActivityHelper` does not exist (compile error is the expected failure mode; that counts).

- [ ] **Step 3: Create the helper**

```csharp
using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Functions;

/// <summary>
/// Spans for the function-execution path (<see cref="FunctionAppService.ExecuteFunctionAsync"/>),
/// which previously produced no phase spans of its own — authorization, request validation,
/// cache-key/generation resolution and response building were all unattributable inside the
/// endpoint transaction. Envelope + per-phase children, always-on Business category.
/// </summary>
public static class FunctionActivityHelper
{
    /// <summary>Source name as a const so test listeners never touch the static field (type-init trap).</summary>
    public const string SourceName = "BBT.Workflow.Functions";

    /// <summary>ActivitySource for function-path spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Operation name for the authorization phase (function access policy).</summary>
    public const string OperationAuthorize = "Function.Authorize";

    /// <summary>Operation name for verb + input-schema validation (may run schema rule scripts).</summary>
    public const string OperationValidateRequest = "Function.ValidateRequest";

    /// <summary>Operation name for response building (representation / IOutputHandler script).</summary>
    public const string OperationBuildResponse = "Function.BuildResponse";

    /// <summary>Starts the envelope span for one function execution, named <c>Function.Execute/{key}</c>.</summary>
    public static Activity? StartExecute(string functionKey)
    {
        var activity = ActivitySource.StartActivity(
            $"Function.Execute/{functionKey}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }

    /// <summary>Starts one phase child (see the Operation* consts).</summary>
    public static Activity? StartPhase(string operationName)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }
}
```

Confirm the namespace matches `FunctionAppService`'s (`BBT.Workflow.Functions` — read the file's namespace line).

- [ ] **Step 4: Wire into FunctionAppService**

In `ExecuteFunctionAsync` (line ~135), wrap the whole body:

```csharp
    private async Task<Result<FunctionResponseOutput>> ExecuteFunctionAsync(
        Function function, /* …existing params… */)
    {
        using var functionActivity = FunctionActivityHelper.StartExecute(function.Key);
        Activity.Current?.SetTag(TelemetryConstants.TagNames.Domain, function.Domain);

        Result<FunctionAccessResult> access; // use the actual return type of AuthorizeAsync
        using (FunctionActivityHelper.StartPhase(FunctionActivityHelper.OperationAuthorize))
        {
            access = await functionAccessPolicy.AuthorizeAsync(
                function, instance, workflow, headers, queryParameters, cancellationToken);
        }
        if (!access.IsSuccess)
            return Result<FunctionResponseOutput>.Fail(access.Error);

        // …verb check + scriptBody + metadata + lazyScriptContext unchanged…

        Result inputValidation; // actual return type from the existing code
        using (FunctionActivityHelper.StartPhase(FunctionActivityHelper.OperationValidateRequest))
        {
            inputValidation = await functionRequestValidationService.ValidateRequestAsync(
                function, body, lazyScriptContext, headers, cancellationToken);
        }
        if (!inputValidation.IsSuccess)
            return Result<FunctionResponseOutput>.Fail(inputValidation.Error);

        // …rest of the method unchanged (cache key, generation, task run, response)…
    }
```

Keep the existing local variable names; only introduce the `using` blocks and hoist the two result variables out of them. In `BuildResponseAsync` (line ~412), wrap the whole method body in `using (FunctionActivityHelper.StartPhase(FunctionActivityHelper.OperationBuildResponse))` and additionally wrap the output-handler invocation:

```csharp
                var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                    function.Output, flowScripts: scriptContext.Workflow?.Scripts, cancellationToken: cancellationToken);

                ScriptResponse scriptResponse; // actual type from existing code
                using (ScriptActivityHelper.StartExecuteActivity("functionOutput"))
                {
                    scriptResponse = await handler.OutputHandler(scriptContext);
                }
```

Update `ScriptActivityHelper.StartExecuteActivity`'s XML doc list of call sites to include "function output handlers".

- [ ] **Step 5: Run tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~FunctionActivityHelperTests|FullyQualifiedName~FunctionAppService"`
Expected: PASS (new tests + any existing FunctionAppService tests).

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Application/Functions/ src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs test/BBT.Workflow.Application.Tests/Functions/
git commit -m "feat(telemetry): Function.Execute envelope + phase spans; function output handler gets Script.Execute"
```

---

### Task 7: G5 — Extension path spans

**Files:**
- Create: `src/BBT.Workflow.Application/Extensions/Services/ExtensionActivityHelper.cs`
- Modify: `src/BBT.Workflow.Application/Extensions/Services/InstanceExtensionService.cs` (`ProcessExtensionsAsync` ~28, `FetchExtensionsFromReferencesAsync` ~177)
- Test: `test/BBT.Workflow.Application.Tests/Extensions/ExtensionActivityHelperTests.cs` (create)

**Interfaces:**
- Consumes: nothing new.
- Produces: `ExtensionActivityHelper` with `public const string SourceName = "BBT.Workflow.Extensions"`; `StartProcess(string workflowKey, ExtensionScope scope)` → span `Extension.Process/{scope}`; `StartResolve(int referenceCount)` → span `Extension.Resolve` with tag `vnext.extension.ref.count`. Task 8 registers the source.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics;
using BBT.Workflow.Extensions.Services; // adjust to the helper's real namespace
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Extensions;

[Collection("TracingDetailLevel")]
public sealed class ExtensionActivityHelperTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Extensions"; // literal — trap

    private readonly ActivityListener _listener;

    public ExtensionActivityHelperTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() { _listener.Dispose(); Activity.Current = null; }

    [Fact]
    public void Process_span_names_scope_and_tags_workflow()
    {
        using var span = ExtensionActivityHelper.StartProcess("loan-disbursement", ExtensionScope.Everywhere);
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Extension.Process/Everywhere");
        span.GetTagItem("vnext.flow.key").ShouldBe("loan-disbursement");
    }

    [Fact]
    public void Resolve_span_tags_reference_count()
    {
        using var span = ExtensionActivityHelper.StartResolve(3);
        span.ShouldNotBeNull();
        span.OperationName.ShouldBe("Extension.Resolve");
        span.GetTagItem("vnext.extension.ref.count").ShouldBe(3);
    }
}
```

Check `ExtensionScope`'s namespace/values before writing (use a real enum member).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ExtensionActivityHelperTests"`
Expected: FAIL (compile error — helper missing).

- [ ] **Step 3: Create the helper and wire it in**

```csharp
using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Extensions.Services; // match InstanceExtensionService's namespace

/// <summary>
/// Spans for instance-data extension enrichment (<see cref="InstanceExtensionService"/>), which
/// previously produced no spans at all: extension-ref resolution and the enrichment envelope were
/// invisible, leaving cache reads like <c>sys-extensions</c> orphaned on the root transaction.
/// </summary>
public static class ExtensionActivityHelper
{
    /// <summary>Source name as a const so test listeners never touch the static field (type-init trap).</summary>
    public const string SourceName = "BBT.Workflow.Extensions";

    /// <summary>ActivitySource for extension spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>Tag: how many extension references a resolve covered.</summary>
    public const string TagRefCount = "vnext.extension.ref.count";

    /// <summary>Starts the envelope span for one enrichment pass, named <c>Extension.Process/{scope}</c>.</summary>
    public static Activity? StartProcess(string workflowKey, ExtensionScope scope)
    {
        var activity = ActivitySource.StartActivity(
            $"Extension.Process/{scope}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TelemetryConstants.TagNames.Flow, workflowKey);
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }

    /// <summary>Starts the span covering extension component-ref resolution (parallel cache fetches).</summary>
    public static Activity? StartResolve(int referenceCount)
    {
        var activity = ActivitySource.StartActivity("Extension.Resolve", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TagRefCount, referenceCount);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }
        return activity;
    }
}
```

Wire-in — `ProcessExtensionsAsync` first line:

```csharp
        using var processActivity = ExtensionActivityHelper.StartProcess(workflow.Key, currentScope);
```

`FetchExtensionsFromReferencesAsync`, after the early-return:

```csharp
        if (extensionReferences.Count == 0)
            return [];

        using var resolveActivity = ExtensionActivityHelper.StartResolve(extensionReferences.Count);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ExtensionActivityHelperTests|FullyQualifiedName~InstanceExtensionService"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Extensions/ test/BBT.Workflow.Application.Tests/Extensions/
git commit -m "feat(telemetry): Extension.Process envelope + Extension.Resolve spans"
```

---

### Task 8: Source registration, docs, and manual trace verification

**Files:**
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json:94`
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json:94`
- Modify: `workers/BBT.Workflow.Workers.Inbox/appsettings.json:50`
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json:45`
- Modify: `docs/runtime/trace-span-tree.md`

**Interfaces:**
- Consumes: `SourceName` values from Tasks 6-7 (`BBT.Workflow.Functions`, `BBT.Workflow.Extensions`); `BBT.Workflow.Execution.Invokers` for the orchestration/worker hosts (Invoke spans only appear on the Execution host, but registering the source everywhere is the established convention — see how `BBT.Workflow.Scripting` appears in all four).
- Produces: exported spans in all hosts; documentation of every new span name.

- [ ] **Step 1: Add the sources to all four hosts**

Append to each `Telemetry:Tracing:AdditionalSources` array (JSON — keep one line style consistent with the file):

```json
"BBT.Workflow.Functions", "BBT.Workflow.Extensions", "BBT.Workflow.Execution.Invokers"
```

Execution host already matches `BBT.Workflow.Execution.Invokers` via its `BBT.Workflow.Execution*` wildcard — add only `Functions`/`Extensions` there. Verify with:

```bash
python3 - <<'EOF'
import json
for p in ["orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json",
          "execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json",
          "workers/BBT.Workflow.Workers.Inbox/appsettings.json",
          "workers/BBT.Workflow.Workers.Outbox/appsettings.json"]:
    s = json.load(open(p))["Telemetry"]["Tracing"]["AdditionalSources"]
    assert "BBT.Workflow.Functions" in s and "BBT.Workflow.Extensions" in s, p
    print("OK", p)
EOF
```

- [ ] **Step 2: Update `docs/runtime/trace-span-tree.md`**

Add the new spans to the span inventory (follow the doc's existing table format), covering: `Task.Resolve`, `Task.Journal.Create`, `Task.Journal.Complete`, `Execution.HandleInvoke`, `Invoke.Prepare`, `Function.Execute/{key}`, `Function.Authorize`, `Function.ValidateRequest`, `Function.BuildResponse`, `Script.Execute` (scriptKind=`functionOutput`), `Extension.Process/{scope}`, `Extension.Resolve`, and the new tags `vnext.task.trigger`, `vnext.extension.ref.count`. Note the two new sources and the baggage-overload fix (existing helpers now chain-parented — the doc's "defect" note for explicit-parent spans should be updated to reflect what was fixed).

- [ ] **Step 3: Full build + targeted test sweep**

```bash
dotnet build
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SpanHelperBaggageTests|FullyQualifiedName~TaskFactoryResolveSpanTests|FullyQualifiedName~TaskExecutionEngineTests|FullyQualifiedName~InvokePrepareSpanTests|FullyQualifiedName~FunctionActivityHelperTests|FullyQualifiedName~ExtensionActivityHelperTests"
```
Expected: build clean, all filtered tests PASS.

- [ ] **Step 4: Manual trace verification (local stack)**

Preconditions: infra up (`cd etc/docker && ./run-docker.sh` — check it isn't already running first), then the 4 apps each with `--launch-profile http` (orchestration 4201, execution 4202, inbox, outbox). Drive one data-function request on any existing local flow (e.g. the `loan-disbursement` data function used in the analysis), then query local Elastic:

```bash
curl -s 'http://localhost:9200/traces-apm*/_search' -H 'Content-Type: application/json' -d '{
  "size": 0,
  "query": {"range": {"@timestamp": {"gte": "now-15m"}}},
  "aggs": {"names": {"terms": {"field": "span.name", "size": 100}}}
}'
```

Acceptance: the aggregation contains `Task.Resolve`, `Task.Journal.Create`, `Task.Journal.Complete`, `Execution.HandleInvoke`, `Invoke.Prepare`, a `Function.Execute/…` entry, `Function.BuildResponse`, and (on an instance read with extensions) `Extension.Process/…`. Then pick one trace id from the run and confirm in the waterfall that (a) `Execution.HandleInvoke` sits between the execution transaction and `Invoke.{type}/{key}`, and (b) `Invoke.Prepare` ends at or before the outbound `GET`/Dapr client span starts.

- [ ] **Step 5: Commit**

```bash
git add orchestration/ execution/ workers/ docs/runtime/trace-span-tree.md
git commit -m "feat(telemetry): register Functions/Extensions/Invokers sources; document full span inventory"
```

---

## Self-review notes

- **Spec coverage:** G0→Task 1, G1→Tasks 2-3, G2→Task 4, G3→Task 5, G4→Task 6, G5→Task 7, registration/docs/verification→Task 8. User decisions honored: all invokers (Task 5, with the CacheAside exception stated and justified), baggage fix in scope (Task 1), all spans Business always-on (every helper sets `SpanCategory=Business`, none gated).
- **Known deliberate gap:** `TaskInvokeHandler` (Task 4) ships without a unit test because no test project references the Execution host; covered by Task 8 Step 4. If the team wants a unit test, adding a host test project is a separate task.
- **Type consistency:** all new consts are defined in the task that introduces them and consumed by name afterwards (`OperationResolve`, `OperationJournalCreate/Complete`, `SourceName` × 2, `TagRefCount`, `TagNames.TaskTrigger`). Tests that reference fixture internals (`TestTaskContexts`, `HttpTaskInvokerContentTypeTests` builders, `ExtensionScope` members) carry explicit "read the real file first" instructions because those signatures were not fully verified during planning.
