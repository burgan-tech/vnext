# Same-key/same-order task journal fix — report

## Diagnosis (given, not re-investigated)

`TaskCoordinator.ExecuteWithDetailsAsync` groups `OnExecuteTask` entries by `Order` and runs a group
of size > 1 through `ExecuteTaskGroupInParallelAsync` (`Task.WhenAll`), passing every task in the
group the **same shared** `TaskEngineExecutionOptions` instance (`.Default` or
`.FreshTransitionRecord`, both `JournalTaskKey == null`). `TaskExecutionEngine` builds
`InstanceTask.ExecutionKey` from `options.JournalTaskKey ?? task.Key` plus transitionId/trigger/order.
Two entries with the same task key at the same order therefore compute the **identical**
`ExecutionKey` and race on `INSERT`, one dying on `UX_InstanceTasks_ExecutionKey` (23505). This is a
race (probe-then-insert isn't atomic), not a sequencing bug, and predates the recent probe-skip perf
change.

## Part 1 — fix

`src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs`:

- New `internal static TaskCoordinator.ResolveGroupEngineOptions(IReadOnlyList<OnExecuteTask>, TaskEngineExecutionOptions)`.
  For each `Order` group (single-task and parallel path alike — computed once, before branching, so
  the decision comes from the definition's shape, not the execution path):
  - A task key that appears once keeps the base options **by reference** (no `with` clone, no churn).
  - A task key that repeats gets `{key}#{position}` on **every** occurrence, positional by order of
    appearance among that key's occurrences (`script-task#0`, `script-task#1`, …).
  - A `JournalTaskKey` the caller already set (e.g. FanOut's own `"key#index"`) is never overwritten
    — checked via `string.IsNullOrEmpty(options.JournalTaskKey)` before assigning.
- `ExecuteTaskGroupInParallelAsync` now takes `IReadOnlyList<TaskEngineExecutionOptions>
  engineOptionsPerTask` (one per task, indexed) instead of one shared instance.
- New `LogDuplicateTaskKeysIfAny` emits the Part 2 warning once per repeated key per group.

Since `ExecutionKey` is derived from `JournalTaskKey ?? task.Key`, giving each occurrence a distinct
`JournalTaskKey` also gives it a distinct `ExecutionKey` and a distinct `InstanceTask.TaskId` journal
row — no changes needed in `TaskExecutionEngine`/`InstanceTask` themselves.

## Part 2 — warning, not error

Checked `WorkflowValidationResult` (`src/BBT.Workflow.Domain/Definitions/Validators/WorkflowValidationResult.cs`):
only `IList<ValidationResult> ValidationErrors` / `AddError` exist — no severity concept at all, hard
errors only. Per instructions, did **not** invent a severity system and did **not** downgrade to a
hard validation error. Instead added `WorkflowLogs.DuplicateTaskKeyAtSameOrder`
(`src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`, new "Task Coordinator" region, EventId **10155**,
`LogLevel.Warning` — next free id after the existing "Fan-Out Execution" region, which ends at 10154)
and call it from `TaskCoordinator.LogDuplicateTaskKeysIfAny` at execution time (once per group, per
repeated key). Message names transition key, hook (`taskTrigger`), task key, occurrence count, order,
and instructs the author to give the entries distinct orders if they're meant as separate steps.

## Tests

- `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskCoordinatorDuplicateTaskKeyTests.cs` —
  black-box, via the public `ExecuteWithDetailsAsync` API + a substituted `ITaskExecutionEngine`
  (same pattern as existing `TaskCoordinatorTests.cs`). Uses the user's exact 4-entry fixture.
  Asserts: two `script-task` occurrences get distinct `script-task#0`/`script-task#1`
  `JournalTaskKey`s; `http-task` (unique, same order-0 group) stays bare; `remote-task` (order 1,
  single-task path) is unaffected; no-duplicate hooks reuse the exact same options instance (no
  churn); warning is logged with the right fields for the duplicate case and NOT logged for a normal
  hook. This file has no dependency on the new internal helper, so it compiles against both pre- and
  post-fix code — it's the primary RED/GREEN pin.
- `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskCoordinatorGroupEngineOptionsTests.cs` —
  direct unit tests of the internal `ResolveGroupEngineOptions` helper (via the project's existing
  `InternalsVisibleTo`). Covers the "pre-set `JournalTaskKey` is never overwritten" rule, which has
  **no path to trigger through the public API** (the coordinator's base options always start with a
  null `JournalTaskKey`) — this file only compiles once the fix exists, so it's additional coverage
  of new production code, not part of the RED gate.

## Verification gates

1. **RED before fix** — stashed `TaskCoordinator.cs` + `WorkflowLogs.cs`, moved the helper-level test
   file out (it references the not-yet-existing internal method), ran the black-box tests:
   ```
   ExecuteWithDetailsAsync_DuplicateTaskKeySameOrder_GivesEachOccurrenceADistinctJournalKey [FAIL]
     observedOptions[scriptFirstDef].JournalTaskKey
         should be "script-task#0"
         but was null
   ExecuteWithDetailsAsync_DuplicateTaskKeySameOrder_LogsWarningWithTransitionHookKeyAndOrder [FAIL]
     matches.Count should be 1 but was 0 (expected exactly one log entry with EventId 10155)
   Failed: 2, Passed: 2, Total: 4
   ```
   (the 2 passes are the no-duplicate-key control tests, correctly unaffected pre-fix).
2. `dotnet build vnext.sln -v q --nologo` → **0 errors** (190 pre-existing warnings, none new from
   my changes beyond ordinary XML-doc style warnings already present elsewhere in the file).
3. **GREEN after fix** (`git stash pop` + restored helper test file):
   `TaskCoordinatorDuplicateTaskKeyTests` + `TaskCoordinatorGroupEngineOptionsTests` →
   **Passed: 8, Failed: 0**.
4. `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` → **16 failing**, all 16 already
   present in the master baseline (`master-failures.txt`, 58 pre-existing failures), including the
   explicitly-ignored `CacheSetL1Tests.Second_latest_read_costs_only_the_generation_read`. `comm -23`
   diff against baseline (minus the known-ignored test) → **empty** — no new failures. In particular
   `InstanceTaskExecutionKeyTests`, `TaskExecutionEngineTests`, `FanOutTaskExecutorTests`,
   `FanOutTaskExecutorMappingTests`, and the pre-existing `TaskCoordinatorTests` all still pass
   unchanged — no journal `TaskId`/`ExecutionKey` assertion broke.

## Files touched

- `src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs`
- `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskCoordinatorDuplicateTaskKeyTests.cs` (new)
- `test/BBT.Workflow.Application.Tests/Tasks/Coordinator/TaskCoordinatorGroupEngineOptionsTests.cs` (new)

Not staged/committed: the user's local `launchSettings.json`/`appsettings.json` tweaks and two
pre-existing untracked `.superpowers/*.md` reports from earlier work today — none of these are mine.
