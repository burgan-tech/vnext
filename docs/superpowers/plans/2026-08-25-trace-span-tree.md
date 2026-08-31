# End-to-End Trace/Span Tree Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every time-consuming mechanism of the transition lifecycle (steps, script compile/execute, locks, context load, validation, cache, data persist) visible as always-on spans in the trace tree.

**Architecture:** vNext-side only (Approach A). Step and task-phase spans lose their `IsVerbose` creation gate and their `[`-prefixed names, so Aether's `BusinessSpanFilterProcessor` (which suppresses only `[`-prefixed DisplayNames) never matches them. New spans are added at single-funnel points: `ScriptEngine.CompileCoreAsync` (compile), `InstanceStatusLock.AcquireAsync` (lock), `TransitionContextFactory` (context load), `TransitionValidationService` (validation), `InstanceDataWriteService` (data persist), plus three script-execution call sites not already delimited by a parent span.

**Tech Stack:** .NET 10, `System.Diagnostics.ActivitySource`, Aether Telemetry (config-driven OTel registration), xUnit + `ActivityListener` test pattern.

**Spec:** `docs/superpowers/specs/2026-08-25-trace-span-tree-design.md`

## Global Constraints

- NO Aether changes. `BusinessSpanFilterProcessor` stays untouched.
- Span names NEVER start with `[` (that prefix is export-suppressed in Business mode).
- Naming convention: `Area.Operation` (`Step.ChangeState`, `Script.Compile`, `Lock.Acquire`).
- All tag name strings live in `TelemetryConstants.TagNames` — no inline tag-name literals.
- No instance data content, headers, or payloads on any span — identifiers and sizes only.
- Existing `ScriptCompileTelemetry` accumulator tags and the `script.compile` event MUST keep working unchanged (query compatibility).
- Every new `ActivitySource` name must be added to `Telemetry:Tracing:AdditionalSources` in the same commit (Task 1 pre-adds them).
- Logging via `WorkflowLogs.cs` extensions only — never raw `logger.Log*` (repo rule).
- Test baseline: master has ~191 pre-existing test failures (AmbientServiceProvider parallel-collection leakage). Judge success by "no NEW failures", comparing against a baseline run.
- Commit messages: Conventional Commits, ending with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## File Structure Overview

| File | Responsibility |
|---|---|
| `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` | + new tag names (step, lock, script, data) |
| `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/PipelineStepActivityHelper.cs` | Always-on step spans; generic `StartOperationActivity` for LoadContext/Validate/Instance spans |
| `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs` | Step outcome tag + error status on step spans |
| `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionActivityHelper.cs` | Remove Verbose gate from phase spans |
| `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs` (NEW) | `BBT.Workflow.Scripting` source; Compile/Execute/ResolveHelpers spans |
| `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs` | Compile + helper-resolve spans |
| `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ResourceLockStep.cs` | `Script.Execute` around lock-key script |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs` | `Script.Execute` around subflow input mapping |
| `src/BBT.Workflow.Application/SubFlow/Services/SubflowOutputMappingService.cs` | `Script.Execute` around subflow output mapping |
| `src/BBT.Workflow.Infrastructure/Execution/Locks/InstanceStatusLock.cs` | `Lock.Acquire` span |
| `src/BBT.Workflow.Infrastructure/Execution/Locks/TransitionLockScopeFactory.cs` | `Lock.Release` span |
| `src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs` | `Transition.LoadContext` + `Instance.Load` spans |
| `src/BBT.Workflow.Application/Execution/Transitions/Validation/TransitionValidationService.cs` | `Transition.Validate` span |
| `src/BBT.Workflow.Infrastructure/Data/InstanceDataWriteService.cs` | `Instance.AppendData` spans |
| 4× host `appsettings.json` | `AdditionalSources` completion |
| `test/BBT.Workflow.Application.Tests/...` | ActivityListener-based tests (existing pattern: `Telemetry/ScriptCompileTelemetryTests.cs`, `Execution/Transitions/Pipeline/PipelineStepActivityHelperTests.cs`) |

### Test harness pattern (used by every task's tests)

All new tests follow the existing pattern in `test/BBT.Workflow.Application.Tests/Telemetry/` — an `ActivityListener` subscribed to the source under test:

```csharp
private static ActivityListener CreateListener(string sourceName, List<Activity> collected)
{
    var listener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == sourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = collected.Add
    };
    ActivitySource.AddActivityListener(listener);
    return listener;
}
```

Read one existing test file first (`PipelineStepActivityHelperTests.cs`) and mirror its fixture/collection attributes exactly — the suite has known parallel-collection issues; do not invent a new fixture style.

---

### Task 1: Close the AdditionalSources config gap (+ pre-register new sources)

The `BBT.Workflow.Tasks`, `BBT.Workflow.SubFlow`, `BBT.Workflow.BackgroundJobs` sources exist in code but are missing from some hosts' `Telemetry:Tracing:AdditionalSources` — their spans are silently never exported there. Also pre-register `BBT.Workflow.Scripting` (created in Task 5). Registering a not-yet-existing source is harmless.

**Files:**
- Modify: `execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json:81`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json:81-88`
- Modify: `workers/BBT.Workflow.Workers.Inbox/appsettings.json:50`
- Modify: `workers/BBT.Workflow.Workers.Outbox/appsettings.json:44`

**Interfaces:**
- Consumes: nothing.
- Produces: all `BBT.Workflow.*` sources exported by every host; later tasks rely on this.

- [ ] **Step 1: Edit the four lists**

Execution (`:81`) — replace the line with:

```json
"AdditionalSources": ["BBT.Workflow.Execution*", "BBT.Workflow.Cache", "BBT.Workflow.Pipeline", "BBT.Workflow.Tasks", "BBT.Workflow.SubFlow", "BBT.Workflow.BackgroundJobs", "BBT.Workflow.Scripting"],
```

Orchestration — the list at `:81-88` already has Pipeline/BackgroundJobs/SubFlow/Tasks/Instances.Events/Cache; append `"BBT.Workflow.Scripting"`.

Inbox (`:50`) — replace with:

```json
"AdditionalSources": ["BBT.Workflow.Workers.*", "BBT.Workflow.Instances.Events", "BBT.Workflow.Pipeline", "BBT.Workflow.Tasks", "BBT.Workflow.SubFlow", "BBT.Workflow.BackgroundJobs", "BBT.Workflow.Cache", "BBT.Workflow.Scripting"],
```

Outbox (`:44`) — already has BackgroundJobs/SubFlow/Tasks/Instances.Events/Cache; append `"BBT.Workflow.Pipeline", "BBT.Workflow.Scripting"`.

- [ ] **Step 2: Verify JSON validity**

Run: `for f in execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json workers/BBT.Workflow.Workers.Inbox/appsettings.json workers/BBT.Workflow.Workers.Outbox/appsettings.json; do python3 -m json.tool "$f" > /dev/null && echo "OK $f"; done`
Expected: four `OK` lines.

- [ ] **Step 3: Check vnext-helm-charts for overrides**

Run: `grep -rn "AdditionalSources" /Users/U0B006/Documents/repos/burgan-tech/vnext-helm-charts/ || echo "no override in helm"`
If the charts override `Telemetry__Tracing__AdditionalSources`, note the finding in the final report for the user — do NOT edit the helm repo.

- [ ] **Step 4: Commit**

```bash
git add execution/BBT.Workflow.Execution.HttpApi.Host/appsettings.json orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json workers/BBT.Workflow.Workers.Inbox/appsettings.json workers/BBT.Workflow.Workers.Outbox/appsettings.json
git commit -m "fix(telemetry): register all BBT.Workflow trace sources in every host"
```

---

### Task 2: New tag names in TelemetryConstants

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs` (inside `TagNames`, after `TriggerTargetInstance` at :165)

**Interfaces:**
- Produces (all later tasks consume these exact names):
  - `StepOrder = "vnext.step.order"`, `StepOutcome = "vnext.step.outcome"`
  - `LockKey = "vnext.lock.key"`, `LockAcquired = "vnext.lock.acquired"`, `LockLeaseSeconds = "vnext.lock.lease_seconds"`
  - `ScriptKind = "vnext.script.kind"`, `ScriptCacheHit = "vnext.script.cache.hit"`, `ScriptHelperCount = "vnext.script.helper.count"`
  - `DataVersion = "vnext.data.version"`, `DataSizeBytes = "vnext.data.size_bytes"`

- [ ] **Step 1: Add the constants**

```csharp
        /// <summary>Lifecycle order of a pipeline step span (see LifecycleOrder).</summary>
        public const string StepOrder = "vnext.step.order";

        /// <summary>Flow-control outcome of a pipeline step: continue | stop | skipTo:{order}.</summary>
        public const string StepOutcome = "vnext.step.outcome";

        /// <summary>Distributed status-lock key (vnext:{domain}:{flow}:{id}).</summary>
        public const string LockKey = "vnext.lock.key";

        /// <summary>Whether the single-attempt status-lock acquire succeeded.</summary>
        public const string LockAcquired = "vnext.lock.acquired";

        /// <summary>Lease seconds requested for the status lock.</summary>
        public const string LockLeaseSeconds = "vnext.lock.lease_seconds";

        /// <summary>What a script span was executing: lockKey | subflowInputMapping | subflowOutputMapping | compilation.</summary>
        public const string ScriptKind = "vnext.script.kind";

        /// <summary>True when the compile was served from the type cache (no Roslyn work).</summary>
        public const string ScriptCacheHit = "vnext.script.cache.hit";

        /// <summary>Number of helper components resolved into a compile's helper set.</summary>
        public const string ScriptHelperCount = "vnext.script.helper.count";

        /// <summary>SemVer version of the instance-data row being appended.</summary>
        public const string DataVersion = "vnext.data.version";

        /// <summary>Serialized byte size of the instance-data payload being appended.</summary>
        public const string DataSizeBytes = "vnext.data.size_bytes";
```

- [ ] **Step 2: Build**

Run: `dotnet build src/BBT.Workflow.Domain --nologo -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.Domain/Logging/TelemetryConstants.cs
git commit -m "feat(telemetry): tag constants for step, lock, script and data spans"
```

---

### Task 3: Pipeline step spans always-on (Business mode)

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/PipelineStepActivityHelper.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionExecutor.cs:134-152`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/PipelineStepActivityHelperTests.cs` (existing — rewrite expectations)

**Interfaces:**
- Consumes: Task 2 constants.
- Produces: `PipelineStepActivityHelper.StartStepActivity(ITransitionStep step): Activity?` (no longer null in Business); `PipelineStepActivityHelper.SetStepOutcome(Activity? activity, StepOutcome outcome): void`; `PipelineStepActivityHelper.StartOperationActivity(string operationName): Activity?` (generic, used by Tasks 7-9).

- [ ] **Step 1: Update the existing tests to the new contract**

Rewrite `PipelineStepActivityHelperTests.cs` (it currently asserts null-in-Business). New assertions, using the ActivityListener harness on source `"BBT.Workflow.Pipeline"`:

```csharp
[Fact]
public void StartStepActivity_InBusinessMode_CreatesExportableSpan()
{
    // Arrange: DetailLevel = Business (default), listener attached
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
    var step = new FakeStep(order: 50, name: "ChangeStateStep");

    // Act
    using (var activity = PipelineStepActivityHelper.StartStepActivity(step))
    {
        Assert.NotNull(activity);
        PipelineStepActivityHelper.SetStepOutcome(activity, StepOutcome.Continue());
    }

    // Assert
    var span = Assert.Single(collected);
    Assert.Equal("Step.ChangeState", span.DisplayName);            // Step suffix trimmed, no '[' prefix
    Assert.False(span.DisplayName.StartsWith("["));
    Assert.Equal(50, span.GetTagItem(TelemetryConstants.TagNames.StepOrder));
    Assert.Equal("continue", span.GetTagItem(TelemetryConstants.TagNames.StepOutcome));
    Assert.Equal(TelemetryConstants.SpanCategories.Business,
        span.GetTagItem(TelemetryConstants.TagNames.SpanCategory));
}

[Fact]
public void SetStepOutcome_SkipTo_RecordsTargetOrder()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
    using (var activity = PipelineStepActivityHelper.StartStepActivity(new FakeStep(30, "RunOnExecuteTasksStep")))
    {
        PipelineStepActivityHelper.SetStepOutcome(activity, StepOutcome.SkipToFinalize());
    }
    Assert.Equal("skipTo:110", collected.Single().GetTagItem(TelemetryConstants.TagNames.StepOutcome));
}
```

`FakeStep` is a minimal `ITransitionStep` stub (Order + Name + `ExecuteAsync` returning `Continue()`); if the existing test file already has one, reuse it. Check `StepOutcome`'s actual member names (`StopPipeline`, `SkipToOrder`) in `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/StepOutcome.cs` before writing `SetStepOutcome`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~PipelineStepActivityHelperTests" --nologo`
Expected: FAIL (`SetStepOutcome` does not exist; activity is null in Business).

- [ ] **Step 3: Rewrite PipelineStepActivityHelper**

Replace the class body (keep the `ActivitySource` field). The obsolete re-rooting doc comment is replaced — the hazard is gone because the spans are now always exported:

```csharp
/// <summary>
/// Starts business-level spans for transition pipeline steps and other pipeline-scoped
/// operations (context load, validation, instance load, data append).
/// <para>
/// Step spans are ALWAYS created (Business and Verbose alike). Names deliberately avoid the
/// legacy <c>[</c> prefix: Aether's BusinessSpanFilterProcessor suppresses <c>[</c>-prefixed
/// DisplayNames at export in Business mode, which both hid the spans and re-rooted their
/// children. Prefix-free names are exported everywhere, so the step's children (task spans,
/// subflow starts, HttpClient calls) attach to a parent that really exists in the trace.
/// </para>
/// </summary>
public static class PipelineStepActivityHelper
{
    /// <summary>ActivitySource for pipeline spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Pipeline");

    /// <summary>Starts the span for a pipeline step, named <c>Step.{Name}</c> (trailing "Step" trimmed).</summary>
    public static Activity? StartStepActivity(ITransitionStep step)
    {
        var activity = ActivitySource.StartActivity(
            $"Step.{TrimStepSuffix(step.Name)}",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.StepOrder, step.Order);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }

    /// <summary>Records the step's flow-control outcome (continue | stop | skipTo:{order}).</summary>
    public static void SetStepOutcome(Activity? activity, StepOutcome outcome)
    {
        if (activity is null) return;
        var value = outcome.StopPipeline ? "stop"
            : outcome.SkipToOrder is { } order ? $"skipTo:{order}"
            : "continue";
        activity.SetTag(TelemetryConstants.TagNames.StepOutcome, value);
    }

    /// <summary>
    /// Starts a business-level span for a pipeline-scoped operation that is not a step
    /// (e.g. Transition.LoadContext, Transition.Validate, Instance.Load, Instance.AppendData).
    /// </summary>
    public static Activity? StartOperationActivity(string operationName)
    {
        var activity = ActivitySource.StartActivity(
            operationName,
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    private static string TrimStepSuffix(string name)
        => name.EndsWith("Step", StringComparison.Ordinal) ? name[..^4] : name;
}
```

Remove the now-unused `using BBT.Aether.Telemetry;` if `AetherTracingRuntime` is no longer referenced.

- [ ] **Step 4: Wire the outcome tag in TransitionExecutor**

In `ExecuteStepWithBoundaryAsync` (:134-152), replace the body:

```csharp
    private async Task<Result<StepOutcome>> ExecuteStepWithBoundaryAsync(
        ITransitionStep step,
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var stepActivity = PipelineStepActivityHelper.StartStepActivity(step);
        try
        {
            var result = await step.ExecuteAsync(context, cancellationToken);
            if (result.IsSuccess)
                PipelineStepActivityHelper.SetStepOutcome(stepActivity, result.Value!);
            else
                stepActivity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

            return result;
        }
        catch (Exception ex)
        {
            stepActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Unhandled exception in step {StepName}", step.Name);
            return Result<StepOutcome>.Fail(Error.Failure(ex.GetType().Name, ex.Message));
        }
    }
```

Also delete the stale comment block above the old `using var stepActivity` (:139-141).

- [ ] **Step 5: Run the tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~PipelineStepActivityHelperTests" --nologo`
Expected: PASS.

- [ ] **Step 6: Sweep for other Verbose-coupling on this source**

Run: `grep -rn "IsVerbose" src/ --include='*.cs'`
Expected remaining hits are NOT in `PipelineStepActivityHelper`. `TaskExecutionActivityHelper` still hits — that is Task 4. Any other hit gating span creation: leave it, but list it in the final report.

- [ ] **Step 7: Commit**

```bash
git add -A src/BBT.Workflow.Application test/BBT.Workflow.Application.Tests
git commit -m "feat(telemetry): always-on pipeline step spans with outcome tags"
```

---

### Task 4: Task phase spans always-on

**Files:**
- Modify: `src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionActivityHelper.cs:99-125`
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/TaskPhaseActivityTests.cs` (new)

**Interfaces:**
- Consumes: nothing new.
- Produces: `TaskExecutionActivityHelper.StartActivity(operationName, taskKey, taskType)` now returns a span in Business mode. `Task.PrepareInput` / `Task.Invoke` / `Task.ProcessOutput` become visible under the existing `Task.Execute.{key}` span (created by Aether's `[Trace]` aspect on `TaskExecutionEngine.ExecuteAsync`).

Note: `ScriptCompileTelemetry.FindTargetActivity` walks up to the first span carrying `TaskKey` — the phase spans carry it, so compile accumulator tags now land on the phase span instead of `Task.Execute.{key}`. That is acceptable and documented; do not "fix" it.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void StartActivity_InBusinessMode_CreatesPhaseSpan()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Tasks", collected);

    using (var activity = TaskExecutionActivityHelper.StartActivity(
               TaskExecutionActivityHelper.OperationPrepareInput, "my-task", "Http"))
    {
        Assert.NotNull(activity);
    }

    var span = Assert.Single(collected);
    Assert.Equal("Task.PrepareInput", span.DisplayName);
    Assert.Equal("my-task", span.GetTagItem(TelemetryConstants.TagNames.TaskKey));
    Assert.Equal(TelemetryConstants.SpanCategories.Business,
        span.GetTagItem(TelemetryConstants.TagNames.SpanCategory));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskPhaseActivityTests" --nologo`
Expected: FAIL (activity is null in Business mode).

- [ ] **Step 3: Remove the gate**

In `StartActivity` (:104-105) delete:

```csharp
        if (!AetherTracingRuntime.IsVerbose)
            return null;
```

and change the category tag at :121 from `SpanCategories.Diagnostic` to `SpanCategories.Business`. Update the method's XML doc: phase spans are business-level, always on. Remove `using BBT.Aether.Telemetry;` if now unused.

- [ ] **Step 4: Run tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TaskPhaseActivityTests|FullyQualifiedName~ScriptCompileTelemetryTests" --nologo`
Expected: PASS (ScriptCompileTelemetryTests confirm the accumulator still lands on a TaskKey-carrying span).

- [ ] **Step 5: Commit**

```bash
git add -A src/BBT.Workflow.Application test/BBT.Workflow.Application.Tests
git commit -m "feat(telemetry): task phase spans (PrepareInput/Invoke/ProcessOutput) always on"
```

---

### Task 5: Script.Compile span (decision reversal) + helper-set resolve span

**Files:**
- Create: `src/BBT.Workflow.Application/Scripting/ScriptActivityHelper.cs`
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptEngine.cs:570-677` (CompileCoreAsync), `:383-470` (helper-set memo-miss branch)
- Modify: `src/BBT.Workflow.Application/Scripting/ScriptCompileTelemetry.cs:7-27` (doc comment only)
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/ScriptCompileSpanTests.cs` (new)

**Interfaces:**
- Consumes: Task 2 constants.
- Produces:
  - `ScriptActivityHelper.ActivitySource` (name `"BBT.Workflow.Scripting"`)
  - `ScriptActivityHelper.StartCompileActivity(): Activity?` → span `Script.Compile`
  - `ScriptActivityHelper.SetCompileResult(Activity?, bool cacheMiss, string status): void`
  - `ScriptActivityHelper.StartExecuteActivity(string scriptKind): Activity?` → span `Script.Execute` (Task 6 consumes)
  - `ScriptActivityHelper.StartResolveHelpersActivity(int helperCount): Activity?` → span `Script.ResolveHelpers`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void CompileActivity_RecordsCacheOutcome()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Scripting", collected);

    using (var activity = ScriptActivityHelper.StartCompileActivity())
    {
        ScriptActivityHelper.SetCompileResult(activity, cacheMiss: false, status: "success");
    }

    var span = Assert.Single(collected);
    Assert.Equal("Script.Compile", span.DisplayName);
    Assert.Equal(true, span.GetTagItem(TelemetryConstants.TagNames.ScriptCacheHit));
    Assert.NotEqual(ActivityStatusCode.Error, span.Status);
}

[Fact]
public void CompileActivity_FailureStatus_MarksError()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Scripting", collected);
    using (var activity = ScriptActivityHelper.StartCompileActivity())
    {
        ScriptActivityHelper.SetCompileResult(activity, cacheMiss: true, status: "compilation_error");
    }
    Assert.Equal(ActivityStatusCode.Error, collected.Single().Status);
}

[Fact]
public void ExecuteActivity_CarriesScriptKind()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Scripting", collected);
    using (ScriptActivityHelper.StartExecuteActivity("lockKey")) { }
    Assert.Equal("lockKey", collected.Single().GetTagItem(TelemetryConstants.TagNames.ScriptKind));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScriptCompileSpanTests" --nologo`
Expected: FAIL (ScriptActivityHelper does not exist).

- [ ] **Step 3: Create ScriptActivityHelper**

```csharp
using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Spans for the script engine: compilation (cold cost incl. helper-set builds) and execution.
/// <para>
/// NOTE: this reverses the earlier "no compile span" decision (2026-08 script-perf work) — a
/// user decision on 2026-08-25 (see docs/superpowers/specs/2026-08-25-trace-span-tree-design.md).
/// The <see cref="ScriptCompileTelemetry"/> accumulator tags and <c>script.compile</c> event are
/// kept alongside for query compatibility.
/// </para>
/// </summary>
public static class ScriptActivityHelper
{
    /// <summary>ActivitySource for script spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Scripting");

    /// <summary>Starts the span covering one compile call (cache hits included — sub-ms, tagged).</summary>
    public static Activity? StartCompileActivity()
    {
        var activity = ActivitySource.StartActivity(
            "Script.Compile", ActivityKind.Internal, Activity.Current?.Context ?? default);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>Stamps the compile outcome; any non-success status marks the span as error.</summary>
    public static void SetCompileResult(Activity? activity, bool cacheMiss, string status)
    {
        if (activity is null) return;
        activity.SetTag(TelemetryConstants.TagNames.ScriptCacheHit, !cacheMiss);
        if (!string.Equals(status, "success", StringComparison.Ordinal))
            activity.SetStatus(ActivityStatusCode.Error, status);
    }

    /// <summary>
    /// Starts the span covering one script invocation at a call site that no existing parent span
    /// delimits (lock-key scripts, subflow mappings). Task input/output mappings are deliberately
    /// NOT wrapped — Task.PrepareInput / Task.ProcessOutput already delimit them.
    /// </summary>
    public static Activity? StartExecuteActivity(string scriptKind)
    {
        var activity = ActivitySource.StartActivity(
            "Script.Execute", ActivityKind.Internal, Activity.Current?.Context ?? default);
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.ScriptKind, scriptKind);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }

    /// <summary>Starts the span covering a helper-set resolve + compile (the invisible ~2s cold cost).</summary>
    public static Activity? StartResolveHelpersActivity(int helperCount)
    {
        var activity = ActivitySource.StartActivity(
            "Script.ResolveHelpers", ActivityKind.Internal, Activity.Current?.Context ?? default);
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.ScriptHelperCount, helperCount);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }
}
```

- [ ] **Step 4: Wrap CompileCoreAsync**

In `ScriptEngine.CompileCoreAsync` (:570), first statement of the method body becomes:

```csharp
        using var compileActivity = ScriptActivityHelper.StartCompileActivity();
        var stopwatch = Stopwatch.StartNew();
```

In the success path, immediately after `var cache = compilation.Compiled ? "miss" : "hit";` (:603) add:

```csharp
            ScriptActivityHelper.SetCompileResult(compileActivity, compilation.Compiled, "success");
```

In each catch block, next to the existing `ScriptCompileTelemetry.Record(...)` call, add the matching result with the same status string used there (`"compilation_error"`, `"invalid_operation"`, `"cancelled"`, `"unexpected_error"`):

```csharp
            ScriptActivityHelper.SetCompileResult(compileActivity, cacheMiss: true, "compilation_error");
```

Do NOT touch the `workflowMetrics.*` or `ScriptCompileTelemetry.Record` calls.

- [ ] **Step 5: Wrap the helper-set memo-miss branch**

In `CompileToInstanceAsync(ScriptCode, …)`, the `else` branch of the memo lookup (`helperSetMemo.TryGetValue` at ~:410; the `else` begins at ~:419) resolves helper sources and builds the helper set — the multi-second cold cost. Wrap the entire `else` body:

```csharp
        else
        {
            using var resolveActivity = ScriptActivityHelper.StartResolveHelpersActivity(effective.Helpers!.Count);
            // ... existing resolve + build + memo-store code, unchanged ...
        }
```

Read the actual branch first; if the resolve logic is not a single `else` block, wrap from the first statement of the miss path through the memo store, keeping variable scoping compilable (declare `helperSources`/`helperSet` outside as they already are).

- [ ] **Step 6: Update ScriptCompileTelemetry's class doc**

Replace the first sentence of the `<summary>` (:8-10): the accumulator is no longer the *only* compile visibility — a dedicated `Script.Compile` span now exists (see `ScriptActivityHelper`); the accumulator stays for span-tag-level queries and back-compat.

- [ ] **Step 7: Run tests + build**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScriptCompileSpanTests|FullyQualifiedName~ScriptCompileTelemetryTests" --nologo && dotnet build src/BBT.Workflow.Application --nologo -v q`
Expected: PASS + Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add -A src/BBT.Workflow.Application test/BBT.Workflow.Application.Tests
git commit -m "feat(telemetry): Script.Compile and Script.ResolveHelpers spans (reverses no-compile-span decision)"
```

---

### Task 6: Script.Execute spans at undelimited call sites

Only three call sites run scripts with no parent span delimiting them. Task mappings are NOT wrapped (Task.PrepareInput/ProcessOutput already delimit them — double-wrapping is noise).

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/ResourceLockStep.cs:52-80` (`ResolveKeyAsync`)
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowStarter.cs:253-269` (input mapping)
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowOutputMappingService.cs:25-65` (`ApplyAsync` mapping section)
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/ScriptCompileSpanTests.cs` (extend — `ExecuteActivity_CarriesScriptKind` from Task 5 already covers the helper; call-site coverage is compile-check + manual trace verification in Task 10)

**Interfaces:**
- Consumes: `ScriptActivityHelper.StartExecuteActivity(string scriptKind)` from Task 5.
- Produces: `Script.Execute` spans with `vnext.script.kind` = `lockKey` | `subflowInputMapping` | `subflowOutputMapping`.

- [ ] **Step 1: ResourceLockStep**

In `ResolveKeyAsync`, wrap the compile + handler execution (the `try` body from `var mapping = await scriptEngine.CompileToInstanceAsync<ITransitionMapping>(` through `var key = result?.ToString();`):

```csharp
        try
        {
            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity("lockKey");

            var mapping = await scriptEngine.CompileToInstanceAsync<ITransitionMapping>(
                lockDef.KeyExpression,
                flowScripts: context.Workflow.Scripts,
                cancellationToken: cancellationToken);
            // ... rest unchanged ...
```

Add `using BBT.Workflow.Scripting;` if missing.

- [ ] **Step 2: SubflowStarter**

Around the mapping-dispatch block at :253-269 (both the `ISubFlowMapping` and `ISubProcessMapping` branches call `CompileToInstanceAsync` then `InputHandler`), wrap the enclosing scope once:

```csharp
            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity("subflowInputMapping");
```

placed immediately before the branch that selects the mapping interface, so both branches are covered by one span.

- [ ] **Step 3: SubflowOutputMappingService**

In `ApplyAsync`, immediately before `var mappingInstance = await scriptEngine.CompileToInstanceAsync<object>(` (:52):

```csharp
        using var scriptActivity = ScriptActivityHelper.StartExecuteActivity("subflowOutputMapping");
```

Mind the scope: the `using` must cover the `OutputHandler` invocation (:60). If the compile and invoke live in a narrower block, place the `using` at that block's top.

- [ ] **Step 4: Build + run nearby tests**

Run: `dotnet build src/BBT.Workflow.Application --nologo -v q && dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowCompletionServiceTests|FullyQualifiedName~ResourceLock" --nologo`
Expected: Build succeeded; no NEW test failures vs baseline.

- [ ] **Step 5: Commit**

```bash
git add -A src/BBT.Workflow.Application
git commit -m "feat(telemetry): Script.Execute spans for lock-key and subflow mapping scripts"
```

---

### Task 7: Lock spans (Lock.Acquire / Lock.Release)

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Execution/Locks/InstanceStatusLock.cs:23-40`
- Modify: `src/BBT.Workflow.Infrastructure/Execution/Locks/TransitionLockScopeFactory.cs:136-142` (`TransitionLockScope.DisposeAsync`)
- Test: `test/BBT.Workflow.Infrastructure.Tests/Execution/Locks/InstanceStatusLockActivityTests.cs` (new; if Infrastructure.Tests has no Locks folder, create it — mirror an existing Infrastructure test class's fixture style)

**Interfaces:**
- Consumes: `PipelineStepActivityHelper.StartOperationActivity` (Task 3) + Task 2 lock constants. Infrastructure already references Application, so the helper is reachable.
- Produces: `Lock.Acquire` span (tags `vnext.lock.key`, `vnext.lock.acquired`, `vnext.lock.lease_seconds`) around every status-lock acquisition — `AcceptAsync`, `ReserveAsync`, `TakeOverAsync`, `ReserveSubflowChainAsync`, `Release*` all funnel through `InstanceStatusLock.AcquireAsync`, so one instrumentation point covers them all. `Lock.Release` span around handle disposal.

- [ ] **Step 1: Write the failing test**

Substitute `IDistributedLockService` with NSubstitute; assert both the acquired and the not-acquired shape:

```csharp
[Fact]
public async Task AcquireAsync_EmitsLockAcquireSpan_WithOutcome()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

    var lockService = Substitute.For<IDistributedLockService>();
    lockService.TryAcquireLockAsync("k1", Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns((IDistributedLockHandle?)null); // contention: not acquired
    var sut = new InstanceStatusLock(lockService,
        Options.Create(new WorkflowExecutionOptions { StatusLockLeaseSeconds = 5 }),
        NullLogger<InstanceStatusLock>.Instance);

    await using var scope = await sut.AcquireAsync("k1");

    var span = Assert.Single(collected, a => a.DisplayName == "Lock.Acquire");
    Assert.Equal("k1", span.GetTagItem(TelemetryConstants.TagNames.LockKey));
    Assert.Equal(false, span.GetTagItem(TelemetryConstants.TagNames.LockAcquired));
}
```

Check `WorkflowExecutionOptions`' actual property/namespace before writing; the logger extension `StatusLockAcquireFailed` requires a real `ILogger<InstanceStatusLock>` — `NullLogger` works.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~InstanceStatusLockActivityTests" --nologo`
Expected: FAIL (no span emitted).

- [ ] **Step 3: Instrument AcquireAsync**

```csharp
    public async Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        CancellationToken cancellationToken = default)
    {
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Lock.Acquire");
        activity?.SetTag(TelemetryConstants.TagNames.LockKey, lockKey);
        activity?.SetTag(TelemetryConstants.TagNames.LockLeaseSeconds, _leaseSeconds);

        // Single attempt by design (review decision): a held lock means a concurrent hop is
        // mid-flip; callers surface that as a conflict (409) or proceed unguarded, and the
        // client retry is the back-pressure mechanism — no in-process wait loop.
        var handle = await distributedLockService.TryAcquireLockAsync(
            lockKey,
            _leaseSeconds,
            cancellationToken);

        activity?.SetTag(TelemetryConstants.TagNames.LockAcquired, handle is not null);

        if (handle is not null)
            return new TransitionLockScope(lockKey, handle, _leaseSeconds, logger);

        logger.StatusLockAcquireFailed(lockKey);
        return TransitionLockScope.NotAcquired(lockKey);
    }
```

Add `using BBT.Workflow.Execution.Pipeline;` and `using BBT.Workflow.Logging;`. Failed acquire is `acquired=false`, NOT an error-status span (contention is an expected outcome).

- [ ] **Step 4: Instrument TransitionLockScope.DisposeAsync**

```csharp
    public async ValueTask DisposeAsync()
    {
        if (_handle is not null)
        {
            using var activity = PipelineStepActivityHelper.StartOperationActivity("Lock.Release");
            activity?.SetTag(TelemetryConstants.TagNames.LockKey, LockKey);
            await _handle.DisposeAsync();
            _logger.LogDebug("Transition lock released for {LockKey}", LockKey);
        }
    }
```

(The reentrant/not-acquired scope has `_handle == null` — correctly emits nothing.)

- [ ] **Step 5: Run tests**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~InstanceStatusLockActivityTests" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/BBT.Workflow.Infrastructure test/BBT.Workflow.Infrastructure.Tests
git commit -m "feat(telemetry): Lock.Acquire/Lock.Release spans on the status lock funnel"
```

---

### Task 8: Transition.LoadContext, Instance.Load and Transition.Validate spans

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs:22-63`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Validation/TransitionValidationService.cs:25-38` (`ValidateAsync`)
- Test: `test/BBT.Workflow.Application.Tests/Telemetry/TransitionContextSpanTests.cs` (new)

**Interfaces:**
- Consumes: `PipelineStepActivityHelper.StartOperationActivity` (Task 3).
- Produces: `Transition.LoadContext` span wrapping the factory railway (component cache `Cache.*` child spans attach automatically); `Instance.Load` span wrapping `GetActiveAsync`; `Transition.Validate` span wrapping `ValidateAsync` with error status on failure.

- [ ] **Step 1: Write the failing test**

Use NSubstitute for `IInstanceRepository`, `IComponentCacheStore`, `IRuntimeInfoProvider`; the simplest observable path is failure (domain check throws) — the span must still be emitted and closed:

```csharp
[Fact]
public async Task CreateAsync_EmitsLoadContextSpan_EvenOnFailure()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

    var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
    runtimeInfo.When(r => r.Check("bad")).Throw(new InvalidOperationException("wrong domain"));
    var sut = new TransitionContextFactory(
        Substitute.For<IInstanceRepository>(),
        Substitute.For<IComponentCacheStore>(),
        runtimeInfo);

    var input = new WorkflowExecutionContext { Domain = "bad", /* fill required members from the type's definition */ };
    var result = await sut.CreateAsync(input, CancellationToken.None);

    Assert.False(result.IsSuccess);
    Assert.Single(collected, a => a.DisplayName == "Transition.LoadContext");
}
```

Read `WorkflowExecutionContext` first and fill its required members minimally; if constructing it is disproportionate, test via the smaller seam instead: assert that `Instance.Load` is emitted by calling the private-path equivalent through `CreateAsync` with mocks that return `Result.Ok` — pick whichever compiles cleanly, but at least one span-emission assertion per new span name must exist.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionContextSpanTests" --nologo`
Expected: FAIL.

- [ ] **Step 3: Instrument the factory**

`CreateAsync` becomes async so the span covers the whole railway:

```csharp
    public async Task<Result<TransitionExecutionContext>> CreateAsync(
        WorkflowExecutionContext input,
        CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Transition.LoadContext");
        var result = await ValidateDomain(input.Domain)
            .BindAsync(_ => RehydrateInstanceAsync(input, cancellationToken))
            .ThenAsync(data => Task.FromResult(ResolveStateAndTransition(data, input)))
            .MapAsync(data => BuildExecutionContext(data, input));

        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
    }
```

`RehydrateInstanceAsync` — wrap only the instance load (the flow fetch already emits `Cache.*` spans):

```csharp
        return componentCacheStore.GetFlowAsync(
                input.Domain, input.WorkflowKey, input.WorkflowVersion, cancellationToken)
            .BindAsync(workflow =>
                LoadInstanceAsync(input.InstanceId, cancellationToken)
                    .MapAsync(instance => (workflow, instance)));
```

with the new private method:

```csharp
    private async Task<Result<Instance>> LoadInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Instance.Load");
        var result = await instanceRepository.GetActiveAsync(instanceId, cancellationToken);
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
    }
```

Verify `GetActiveAsync`'s exact return type (`Task<Result<Instance>>`) in `IInstanceRepository` and match it. Add `using System.Diagnostics;` and `using BBT.Workflow.Execution.Pipeline;`.

- [ ] **Step 4: Instrument ValidateAsync**

In `TransitionValidationService.ValidateAsync` (:25), wrap the existing body:

```csharp
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Transition.Validate");
        // existing body ...
        // before each failure return (or once at a single exit point if the method has one):
        //   activity?.SetStatus(ActivityStatusCode.Error, error.Message);
```

Read the method body first; if it delegates to `ValidatePolicyAsync`/`ValidateSchemaAsync`, the wrapper on `ValidateAsync` alone is enough — do not wrap the inner methods too.

- [ ] **Step 5: Run tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~TransitionContextSpanTests" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/BBT.Workflow.Application test/BBT.Workflow.Application.Tests
git commit -m "feat(telemetry): LoadContext, Instance.Load and Validate spans"
```

---

### Task 9: Instance.AppendData span

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Data/InstanceDataWriteService.cs:67-…` (`AppendAsync`), `:112-…` (`AppendExplicitAsync`)
- Test: `test/BBT.Workflow.Infrastructure.Tests/Data/InstanceDataWriteServiceActivityTests.cs` (new; if the write service's dependencies make unit construction disproportionate — it talks raw Npgsql — a listener test against a thin extracted helper is acceptable: extract `private static Activity? StartAppendActivity(string version, long sizeBytes)` and unit-test THAT, keeping the service methods calling it)

**Interfaces:**
- Consumes: `PipelineStepActivityHelper.StartOperationActivity`, Task 2 data constants.
- Produces: `Instance.AppendData` span around each append, tags `vnext.data.version`, `vnext.data.size_bytes`.

- [ ] **Step 1: Read both methods fully**, identify where the serialized payload and version are available (the service persists a JSON payload — take `sizeBytes` from the serialized string's `Encoding.UTF8.GetByteCount`, or the byte[] length if it already holds bytes; never log the payload).

- [ ] **Step 2: Write the failing test** for the extracted helper (or the service if constructible):

```csharp
[Fact]
public void StartAppendActivity_CarriesVersionAndSize()
{
    var collected = new List<Activity>();
    using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
    using (InstanceDataWriteService.StartAppendActivity("1.2.3", 2048)) { }
    var span = Assert.Single(collected);
    Assert.Equal("Instance.AppendData", span.DisplayName);
    Assert.Equal("1.2.3", span.GetTagItem(TelemetryConstants.TagNames.DataVersion));
    Assert.Equal(2048L, span.GetTagItem(TelemetryConstants.TagNames.DataSizeBytes));
}
```

(Make the helper `internal static` + `InternalsVisibleTo` if the test project lacks access — check how Infrastructure.Tests already accesses internals.)

- [ ] **Step 3: Run test to verify it fails.** `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~InstanceDataWriteServiceActivityTests" --nologo` → FAIL.

- [ ] **Step 4: Implement** — helper:

```csharp
    internal static Activity? StartAppendActivity(string version, long sizeBytes)
    {
        var activity = PipelineStepActivityHelper.StartOperationActivity("Instance.AppendData");
        activity?.SetTag(TelemetryConstants.TagNames.DataVersion, version);
        activity?.SetTag(TelemetryConstants.TagNames.DataSizeBytes, sizeBytes);
        return activity;
    }
```

and wrap the body of `AppendAsync` and `AppendExplicitAsync` in `using var activity = StartAppendActivity(version, sizeBytes);` at the point where version/size are known (start the span at method entry with the tags set later via `activity?.SetTag` if the values materialize mid-method).

- [ ] **Step 5: Run tests.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src/BBT.Workflow.Infrastructure test/BBT.Workflow.Infrastructure.Tests
git commit -m "feat(telemetry): Instance.AppendData span on the instance-data write funnel"
```

---

### Task 10: Cache-span verification + documentation

**Files:**
- Read/verify: `src/BBT.Workflow.Application/Caching/CacheActivityHelper.cs`, `src/BBT.Workflow.Application/Caching/CacheSet.cs:91/121/160/316`, `src/BBT.Workflow.Infrastructure/**/RuntimeCacheBackend.cs`
- Create: `docs/runtime/trace-span-tree.md`
- Modify: `docs/README.md` (navigation entry under the runtime/observability grouping)

**Interfaces:** none — verification + docs.

- [ ] **Step 1: Verify cache spans meet the spec**

Check three claims and fix only what fails:
1. No `CacheSet`/`CacheActivityHelper` span name starts with `[` and none is gated on `IsVerbose` (grep both files).
2. An L1 hit produces a span tagged `cache.l1_hit=true` (spec: L1 hits are spans with a tag, not suppressed).
3. When `l1_hit=false`, the L2 (Dapr) read duration is observable — either as a child span from the backend call or as the `CacheSet` span's own duration. If the Dapr client emits no span and `CacheSet`'s span covers more than the backend call, add `using var activity = CacheActivityHelper.…` around the backend get in `RuntimeCacheBackend` following that helper's existing conventions.

- [ ] **Step 2: Write `docs/runtime/trace-span-tree.md`**

Content: the target span tree diagram (copy from the spec §4), the full span-name → source → tags table (every name this plan introduced or ungated: `Step.*`, `Task.PrepareInput/Invoke/ProcessOutput`, `Task.Execute.{key}`, `Script.Compile/Execute/ResolveHelpers`, `Lock.Acquire/Release`, `Transition.LoadContext/Validate`, `Instance.Load/AppendData`, `Cache.*`, `Subflow.*`, `PostCommit.*`), the AdditionalSources registration rule ("new source ⇒ same-commit appsettings update in all four hosts"), and a note that the compile-span decision from the 2026-08 script-perf work was reversed on 2026-08-25 (link the spec).

- [ ] **Step 3: Link it from `docs/README.md`** in the same grouping style as `docs/runtime/state-function-cache-and-etag.md`.

- [ ] **Step 4: Commit**

```bash
git add docs/ src/
git commit -m "docs(telemetry): trace span tree reference; verify cache span L1/L2 visibility"
```

---

### Task 11: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Full build.** `dotnet build --nologo -v q` → Build succeeded, 0 errors.

- [ ] **Step 2: Full test run with baseline comparison.**

Run `dotnet test --nologo 2>&1 | tail -20` on the feature branch AND note the pass/fail counts. Baseline: master has ~191 pre-existing failures (AmbientServiceProvider parallel-collection leakage). Success criterion: **no NEW failing test names**. If unsure whether a failure is new, `git stash` the work, run the same filter on master, compare.

- [ ] **Step 3: Manual trace verification (user-assisted; propose, do not auto-start the heavy stack).**

Report to the user that the code work is done and the following manual verification remains (per CLAUDE.local: check infra state first, don't restart what's running):
1. `cd etc/docker && ./run-docker.sh` (only if infra is not already up)
2. Run the 4 apps with `--launch-profile http`
3. Execute one manual transition + one subflow flow from vnext-example against `http://localhost:4201`
4. In Jaeger (docker), verify: step spans visible in Business mode, task phases nest under `Task.Execute.{key}` under the step group, `Script.Compile` shows `cache.hit`, `Lock.Acquire` on admission, `Transition.LoadContext` with `Cache.*`/`Instance.Load` children, and — critically — the FlatLane/hop chain (`vnext.hop.predecessor`, lane anchor) is intact and nothing re-rooted.

- [ ] **Step 4: Update the user's memory** (session-level note, not a commit): the script-perf memory (`script-perf-analysis-2026-08.md` / `script-compile-cache-cold-cost.md`) records "span EKLENMEYECEK (kullanıcı kararı)" — amend it: reversed 2026-08-25 by user decision, `Script.Compile` span now exists.

---

## Self-Review (completed)

- **Spec coverage:** §5 rows 1-10 map to Tasks 1-10; §7 testing maps to per-task TDD steps + Task 11; §8 rollback needs no task (single revert); §3 approach constraints are Global Constraints.
- **Placeholder scan:** Steps that say "read the file first" (Task 5 Step 5, Task 8 Step 4, Task 9 Step 1) are deliberate read-before-edit instructions with the concrete code to apply given — not deferred design.
- **Type consistency:** `StartOperationActivity` (Task 3) is consumed by Tasks 7, 8, 9 with the same signature; `StartExecuteActivity(string)` (Task 5) consumed by Task 6; constants (Task 2) referenced by exact name everywhere.
