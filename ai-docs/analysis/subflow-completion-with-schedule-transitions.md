# Subflow Completion with Schedule Transitions - Analysis Report

## Problem Statement

When a subflow is started and the child flow's target state has schedule transitions, the child flow cannot report its completion to the parent even though the instance reaches `Completed` status.

## Executive Summary

After thorough analysis, **three root cause candidates** and **two contributing design risks** were identified. The most likely root cause is the interaction between `InstanceCompletedCleanupEventHook` and `InstanceSubCompletedEventHook` within the same UoW commit, combined with the `HookedDistributedEventBus` suppression behavior.

---

## Architecture Context

### Pipeline Step Execution Order

```
 5  HandleCancelPreflightStep
 9  HandleUpdateDataPreflightStep
10  ForwardToActiveSubflowStep
19  SetBusyStep
20  CreateTransitionRecordStep
25  ResourceLockStep
30  RunOnExecuteTasksStep
37  ApplyTimeoutStateStep
39  CancelScheduledJobsStep
40  RunOnExitTasksStep
50  ChangeStateStep
60  RunOnEntryTasksStep
70  HandleSubFlowStep
79  ClearBusyOnResumeStep
80  ScheduleTransitionsStep      ← SCHEDULE
90  RunAutomaticTransitionsStep  ← AUTO
100 HandleFinishStep             ← FINISH
110 FinalizeTransitionStep
112 ResolveAvailableStep
```

### Subflow Completion Flow

1. Child flow finishes → `HandleFinishStep` → `Instance.Complete(domain)`
2. `Instance.Complete()` adds two distributed events:
   - `InstanceCompletedCleanupEvent` (cancel scheduled jobs)
   - `InstanceSubCompletedEvent` (notify parent)
3. `TransitionRunner` commits UoW → Aether dispatches events via `HookedDistributedEventBus`
4. Hooks run synchronously:
   - `InstanceCompletedCleanupEventHook` → cancels scheduled jobs
   - `InstanceSubCompletedEventHook` → calls `SubflowCompletionService.CompletionAsync`
5. If hooks succeed → **inner bus publish is suppressed** (no outbox entry)

---

## Root Cause Candidates

### RC-1: Hook Execution Order + UoW Conflict (HIGH probability)

**Location:** `HookedDistributedEventBus.ExecuteHooksAsync` → `InstanceCompletedCleanupEventHook` → `InstanceSubCompletedEventHook`

**The Problem:**

When `Instance.Complete()` is called, **two** distributed events are raised on the same aggregate within the same UoW:

```csharp
// Instance.cs:287-315
AddDistributedEvent(new InstanceCompletedCleanupEvent { ... });

if (IsSubItem)
{
    AddDistributedEvent(new InstanceSubCompletedEvent { ... });
}
```

Both events are dispatched when the `TransitionRunner`'s UoW commits. The `InstanceCompletedCleanupEventHook` creates its **own isolated DI scope and `RequiresNew` UoW** inside `ProcessLocalAsync`:

```csharp
// InstanceCompletedCleanupEventHook.cs:82-98
await scopeFactory.ExecuteWithWorkflowAsync(eventData.Domain, eventData.Flow, eventData.Version,
    async (sp, ct) =>
    {
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var unitOfWorkManager = sp.GetRequiredService<IUnitOfWorkManager>();
        var cancellationService = sp.GetRequiredService<IInstanceCancellationService>();

        using (currentSchema.Use(eventData.Flow))
        {
            await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew
            }, ct);

            await cancellationService.ProcessCancellationAsync(eventData.InstanceId, ct);
            await uow.CommitAsync(ct);
        }
    }, cancellationToken);
```

**Risk:** If the cleanup hook's inner UoW (`RequiresNew`) modifies the `Instance` entity (e.g., deleting `InstanceJob` records linked to the instance), and the **outer UoW** (from `TransitionRunner`) has already loaded and tracked the same `Instance`, a **concurrency conflict** or **transaction deadlock** could prevent the outer commit from completing cleanly. This is especially dangerous when there are scheduled jobs to cancel (i.e., **when the flow has schedule transitions**).

**Why it correlates with the symptom:** The problem **only** occurs when the child flow has schedule transitions because:
- Schedule transitions → `ScheduleTransitionsStep` creates `InstanceJob` records
- `InstanceCompletedCleanupEvent` hook → cancels/deletes those jobs in a new UoW
- This inner UoW may lock rows that the outer UoW's commit also needs
- If this causes the outer commit to fail/hang, `InstanceSubCompletedEvent` is never dispatched to the parent

### RC-2: Hook Success Suppresses Bus Publication (HIGH probability)

**Location:** `HookedDistributedEventBus.PublishAsync` lines 92-109

```csharp
var hookResult = await ExecuteHooksAsync(payload, cancellationToken);

// If all hooks succeeded, event is handled - don't publish to inner bus
if (hookResult.HooksExecuted && hookResult.AllSucceeded)
{
    return;  // ← EVENT IS SWALLOWED
}
```

**The Problem:**

When `InstanceSubCompletedEventHook` succeeds, the event is **not** published to the inner bus (outbox). This means:

1. If the hook's `SubflowCompletionService.CompletionAsync` succeeds **partially** — e.g., correlation is completed and committed, but `ResumePipelineAsync` fails — there is **no retry path** via the inbox worker.

2. Looking at `SubflowCompletionService.CompletionAsync`:

```csharp
// Phase 1: Complete correlation (committed)
await correlationUow.CommitAsync(cancellationToken);

// Phase 2: Resume pipeline (separate execution)
await ResumePipelineAsync(parentInstance, parentWorkflow!, ...);
```

If Phase 1 succeeds but Phase 2 throws, the service **reverts correlation** in `RevertCorrelationInNewUowAsync`. However, since the hook already returned `Ok()` to `HookedDistributedEventBus`, the bus considers the event "handled" and **does not** send it to the outbox. The inbox handler **never runs**.

**But there's a subtlety:** If the hook throws an exception (not just returns `Fail`), the catch block in `HookedDistributedEventBus` increments `failureCount`, which means the event **would** be published to the inner bus. However, `InstanceSubCompletedEventHook` wraps everything in try/catch and returns `EventHookResult.Fail(ex, ...)`, which **counts as a hook failure** → event gets published to inner bus. This is the **fallback** path.

The risk is when the hook completes **successfully** (returns `Ok`) but the actual work (pipeline resume) fails afterward. In that case: hook returned `Ok`, bus suppressed the publish, and the retry via inbox never happens.

### RC-3: Schedule Timer Job Fires on Completed Instance (MEDIUM probability)

**Location:** `TransitionTimerJobHandler.HandleAsync` → `WorkflowExecutionService.ExecuteTransitionAsync`

**The Problem:**

There is a **race condition** between:
1. Child flow reaching `Finish` state → `HandleFinishStep` → `Instance.Complete()`
2. A scheduled timer job firing for the same child instance

The sequence:
1. Child flow enters a state with schedule transitions
2. `ScheduleTransitionsStep` (order 80) enqueues a timer job
3. Child flow continues through auto transitions to `Finish`
4. `HandleFinishStep` (order 100) sets `Instance.Complete()` and adds events
5. **Before** UoW commits, the timer job fires in a background thread

When the timer job fires:
```csharp
// TransitionTimerJobHandler.cs:47-63
var executionContext = input.ToExecutionContext(
    args.InstanceId.ToString(),
    args.Version,
    args.TransitionKey);

executionContext.TriggerType = TriggerType.Scheduled;
executionContext.IsReentry = true;
await workflowExecutionService.ExecuteTransitionAsync(executionContext, cancellationToken);
```

The timer handler acquires a **distributed lock** on the same instance. If the timer fires while the completion pipeline is still running (inside the lock), it will fail to acquire the lock. But if the timer fires **after** the lock is released but **before** the `InstanceCompletedCleanupEvent` hook cancels the jobs, you get a race:

- Timer job loads the instance, sees it's `Completed`, but `GetActiveAsync` may still return it (depending on implementation)
- Timer job tries to execute a transition on a completed instance → may fail with validation error → exception → `finally` block marks job as processed

This race is mitigated somewhat by the `CancelScheduledJobsStep` (order 39) which cancels jobs when **leaving** a state, but:
- If the transition path goes directly from Schedule → Auto → Finish within the **same pipeline run** (chained transitions), the cancel only happens for the **first** state's jobs, not intermediate states that may have been scheduled.
- The `InstanceCompletedCleanupEvent` is the safety net, but it fires **after** UoW commit, not during the pipeline.

---

## Contributing Design Risks

### DR-1: `HasOnlyManualOrEventTransitions` Logic Bug

**Location:** `State.cs:201-203`

```csharp
public bool HasOnlyManualOrEventTransitions => 
    !Transitions.Any() || 
    Transitions.Any(t => t.TriggerType == TriggerType.Manual || 
                         t.TriggerType == TriggerType.Event || 
                         t.TriggerType == TriggerType.Scheduled);
```

**Issue:** This property returns `true` if the state has **any** manual/event/scheduled transition, even if it **also** has automatic transitions. The logic should be `All()` instead of `Any()`:

```csharp
// What it probably should be:
public bool HasOnlyManualOrEventTransitions => 
    !Transitions.Any() || 
    Transitions.All(t => t.TriggerType == TriggerType.Manual || 
                         t.TriggerType == TriggerType.Event || 
                         t.TriggerType == TriggerType.Scheduled);
```

However, `ResolveAvailableStep` has an **earlier guard** (`!context.Target.HasOnlyManualOrEventTransitions` at line 121) that checks this, and since the auto transition check (`NextTransition != null`) precedes it, this may be masked in most cases. But in edge cases where an auto transition condition is not met and the state has both manual and scheduled transitions, the instance could incorrectly be set to Active.

**Note:** The test `HasOnlyManualOrEventTransitions_ShouldReturnFalse_WhenHasScheduledTransition` in `StateTests.cs` contradicts the name — it asserts `False` for a state that has a manual + scheduled transition, but the code **would return `True`** since `Any(Manual)` is true. This suggests the property name is misleading or the tests are checking the wrong assertion.

### DR-2: SubflowCompletionService's Two-Phase Commit Risk

**Location:** `SubflowCompletionService.cs:73-162`

The service performs a **two-phase operation** without transactional guarantees across both phases:

- **Phase 1** (inside `correlationUow`): Complete correlation + output mapping → commit
- **Phase 2** (outside UoW): `ResumePipelineAsync` → `WorkflowExecutionService.ExecuteTransitionAsync`

If Phase 2 fails, `RevertCorrelationInNewUowAsync` attempts to roll back Phase 1. However:
- The revert opens a **new UoW** with a **fresh DbContext** — it modifies the same `parentInstance` object in memory, which was loaded in Phase 1's DbContext (now disposed)
- If Aether's EF Core change tracking requires a tracked entity, the revert may silently do nothing
- If the revert itself fails (e.g., DB connection issue), the correlation stays "completed" but the pipeline was never resumed → **orphaned parent** waiting for a subflow that's already done

---

## Scenario Reconstruction

Here's the most likely failure scenario matching the described symptoms:

```
1. Parent flow transitions to SubFlow state
   → HandleSubFlowStep creates correlation, parent goes Busy
   → StartSubflowJob starts child flow

2. Child flow progresses through states
   → Enters a state with schedule transitions
   → ScheduleTransitionsStep (80) creates InstanceJob + Dapr timer
   
3. Child flow reaches Finish state via auto transitions
   → HandleFinishStep (100) calls Instance.Complete()
   → Instance.Complete() raises:
     a) InstanceCompletedCleanupEvent
     b) InstanceSubCompletedEvent (because IsSubItem=true)

4. TransitionRunner commits UoW
   → Aether's domain event pipeline dispatches events to HookedDistributedEventBus

5. InstanceCompletedCleanupEventHook fires FIRST
   → Opens new scope + RequiresNew UoW
   → ProcessCancellationAsync deletes/cancels InstanceJobs
   → Commits inner UoW
   
6. InstanceSubCompletedEventHook fires SECOND
   → Calls SubflowCompletionService.CompletionAsync
   → Phase 1: Complete correlation, commit
   → Phase 2: ResumePipelineAsync
     → TransitionRunner opens new scope + UoW
     → Pipeline runs from ClearBusyOnResumeStep (79)
     → Steps 79, 80 (Schedule), 90 (Auto), 100 (Finish), 110 (Finalize), 112 (Resolve)
     
7. FAILURE POINT: The parent's current state has schedule transitions
   → ScheduleTransitionsStep (80) runs and tries to create new scheduled jobs
   → If this step fails (e.g., timer evaluation error, DB constraint), 
     the pipeline returns Fail
   → SubflowCompletionService catches the error, reverts correlation
   → But the hook returned Fail → event goes to inner bus
   → BUT: the revert may not work correctly (DR-2)
   
   OR:
   
   → ScheduleTransitionsStep succeeds
   → Auto transition runs, no winner
   → HandleFinishStep skips (parent target is not Finish)
   → Pipeline completes successfully
   → BUT: parent instance status never changes from Busy to Active
     because ResolveAvailableStep sees HasOnlyManualOrEventTransitions=true (DR-1 bug)
     AND/OR the state has scheduled transitions so the instance stays Busy
```

---

## Recommended Investigation Steps

1. **Check logs** for the specific instance:
   - Look for `InstanceCompletedCleanupHookProcessing` and `SubFlowEventReceived` log entries
   - Check if `SubFlowPipelineResumed` appears after `SubFlowCorrelationCompleted`
   - Look for any `SubFlowCompletionFailed` entries

2. **Check parent instance state in DB**:
   - Is the parent still `Busy`?
   - Is the correlation still `Active` or `Completed`?
   - What is the parent's `CurrentState`?

3. **Check if timer jobs still exist** for the child instance after completion

4. **Add diagnostic logging** to `HookedDistributedEventBus` to trace the exact order of hook execution and whether the inner bus publish is suppressed

5. **Test the `HasOnlyManualOrEventTransitions` property** with a state that has both manual and scheduled transitions — verify if `ResolveAvailableStep` incorrectly sets Active

---

## Recommended Fixes

### Fix 1: Ensure Event Bus Fallback for Critical Events

For `InstanceSubCompletedEvent`, even when the hook succeeds, the event should **always** be published to the outbox as a safety net. This ensures the inbox handler can retry if the hook's work (pipeline resume) fails downstream.

### Fix 2: Fix `HasOnlyManualOrEventTransitions` Logic

Change `Any()` to `All()` to correctly identify states that truly have **only** manual/event/scheduled transitions.

### Fix 3: Add Concurrency Protection

Add optimistic concurrency (e.g., `ConcurrencyStamp`) to the `Instance` entity to detect and handle concurrent modifications between the cleanup hook's inner UoW and the outer UoW.

### Fix 4: Consolidate Two-Phase Commit in SubflowCompletionService

Consider making the correlation completion and pipeline resume atomic, or at minimum, ensure the revert mechanism works with a fresh entity load from the database instead of reusing the in-memory `parentInstance`.
