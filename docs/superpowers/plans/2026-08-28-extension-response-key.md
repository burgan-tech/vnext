# Extension Response Key Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let two extensions share one task definition. Today the second execution's output collides with the first under a task-derived variable name — loudly on the parallel path (a Preprod fault) and silently on the sequential one.

**Architecture:** `TaskEngineExecutionOptions` gains a `ResponseVariableKey`, mirroring the `JournalTaskKey` seam that already disambiguates repeated task keys. `TaskExecutorBase` uses it in place of `taskKey.ToVariableName()` for both response writes. `InstanceExtensionService` sets it to the extension's key and reads the same key back. Transition-scoped tasks never set it and are byte-identical to today.

**Tech Stack:** .NET 10, xUnit + Shouldly + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-08-28-extension-response-key-spec.md` — read it. In particular the finding that `OnExecuteTask` carries a per-entry `Mapping`, which is why deduplicating a shared task is the wrong fix.

## Global Constraints

- **Local commits only. NEVER `git push`.** No branch/merge/rebase.
- **The working tree carries files that are NOT yours** — the user's local-environment edits (`launchSettings.json`, `appsettings.json`) and files dirtied by a concurrent session. Stage only the files your task modifies; run `git diff --staged --stat` before every commit and confirm each staged file is one you edited. Never `git add -A` / `git commit -a`.
- **Transition-scoped tasks must not change.** `onExecute` / `onEntry` / `onExit` tasks keep `taskKey.ToVariableName()` for `TaskResponse`, and keep their journal keys. Any diff that alters those is a defect, not an improvement — a reviewer will check for it explicitly.
- **No deduplication of shared tasks.** Two extensions referencing one task run it twice on purpose; their `Mapping`s differ.
- Public members get XML `<summary>` docs; comments explain WHY. Match the voice of the file you edit.
- Logging goes through `WorkflowLogs.cs` `[LoggerMessage]` extensions — never raw `logger.Log*`.
- Regression gate: `dotnet build vnext.sln -v q --nologo` → 0 errors; `dotnet test test/BBT.Workflow.Application.Tests --nologo -v q` and `dotnet test test/BBT.Workflow.Domain.Tests --nologo -v q` with **no NEW failing test name** versus a baseline you capture yourself BEFORE your change. Known-stale: an old `master-failures.txt` in a scratchpad — do not use it. Recent baselines were Application 16 / Domain 27, but capture your own.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/BBT.Workflow.Application/Tasks/Coordinator/TaskEngineExecutionOptions.cs` | New `ResponseVariableKey` | 2 |
| `src/BBT.Workflow.Application/Tasks/Executors/Core/TaskExecutorBase.cs` | Uses it for both response writes | 2 |
| `src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs` | Accepts caller-supplied per-task options; respects a caller-set `ResponseVariableKey` | 2 |
| `src/BBT.Workflow.Application/Tasks/Coordinator/ITaskCoordinatorExtended.cs` | The overload that carries them | 2 |
| `src/BBT.Workflow.Application/Extensions/Services/InstanceExtensionService.cs` | Sets the key; reads by extension | 3 |
| `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` | Hook-aware duplicate warning | 4 |
| `test/BBT.Workflow.Application.Tests/…` | Tests per task | 1-4 |

**Task 1 is an inventory, and it gates the rest.** Do not start Task 2 before it is answered.

---

### Task 1: Establish whether any consumer reads an extension task's response by task key

**Why this is first.** The change moves where an extension task's output is filed. The spec judges that acceptable, but explicitly requires it be *verified rather than assumed* — an authored `.csx` in a domain package that reads `TaskResponse["someExtensionTask"]` would break silently.

**Files:** none modified. Produces `docs/runtime/extension-response-key-inventory.md`.

- [ ] **Step 1: Inventory the consumers**

Search, and record the exact commands and their output:
- `.csx` scripts in `/Users/U0B006/Documents/repos/burgan-tech/vnext-example` referencing `TaskResponse` or `OutputResponse` by key.
- The same in `docs/` and `ai-docs/` in this repo — is `context.TaskResponse["..."]` a *documented* script surface? If it is documented, that raises the bar regardless of what the example repo does.
- Every in-repo read of `OutputResponse` and `TaskResponse` outside `ScriptContext` itself.
- Whether `vnext-docs` (`/Users/U0B006/Documents/repos/burgan-tech/vnext-docs`) documents it for domain authors.

- [ ] **Step 2: Write the finding**

`docs/runtime/extension-response-key-inventory.md`: what you searched, what you found, and a one-line verdict — **safe** (no consumer reads an extension task's response by task key) or **breaking** (name them).

If **breaking**, stop and report rather than continuing: the spec's accepted risk assumed otherwise, and the remedy changes (parallel support for both keys plus a `deprecations.json` entry, per the repo's no-breaking-change policy).

- [ ] **Step 3: Commit** the inventory document.

---

### Task 2: Thread a response variable key through the executor

**Files:**
- Modify: `TaskEngineExecutionOptions.cs`, `TaskExecutorBase.cs`, `TaskCoordinator.cs`, `ITaskCoordinatorExtended.cs`
- Test: `test/BBT.Workflow.Application.Tests/Tasks/…` (find where `ResolveGroupEngineOptions` is already tested and extend that neighbourhood; say which file you chose)

**Interfaces:**
- `TaskEngineExecutionOptions` gains `public string? ResponseVariableKey { get; init; }` — null means "derive from the task key", exactly as `JournalTaskKey` does.
- The coordinator gains a way for a caller to supply per-task options. Two shapes are acceptable; **pick one, and say why in your report**:
  (a) an overload taking `IReadOnlyList<(OnExecuteTask Task, TaskEngineExecutionOptions Options)>`, or
  (b) an optional `Func<OnExecuteTask, TaskEngineExecutionOptions, TaskEngineExecutionOptions>` refiner applied per task.
  Whichever you choose, `ResolveGroupEngineOptions` must **respect a caller-set `ResponseVariableKey`** the same way it already respects a caller-set `JournalTaskKey` (`TaskCoordinator.cs:479-486`), and must not invent one on its own.

- [ ] **Step 1: Capture your regression baseline** (both suites), then write the failing tests.

Pin:
- `ResolveGroupEngineOptions` leaves a caller-set `ResponseVariableKey` untouched, including when the same task key appears twice in the group (the `JournalTaskKey` disambiguation must still happen alongside it, not instead of it).
- With `ResponseVariableKey` null, the variable name is `taskKey.ToVariableName()` — today's behavior, for a transition-scoped task.
- With it set, both `TaskResponse` and `OutputResponse` are written under that key instead. **Both**: the Preprod throw came from the `TaskResponse` merge (`Models.cs:828`), so keying only `OutputResponse` would leave the crash in place.

- [ ] **Step 2: Run the tests, confirm they fail**, paste the failure.

- [ ] **Step 3: Implement.**

In `TaskExecutorBase`, replace the two derivations:
- line ~132 `context.ScriptContext.SetOutputResponse(outputResult.Value, taskKey.ToVariableName())`
- line ~338 `var variableKey = taskKey.ToVariableName();`

with the option when present. Read the real code first — `UpdateScriptContextWithResponse` is `static` and takes `taskKey`, so the options value has to reach it; extending its parameter list is fine, but check every caller.

Comment the WHY at the option's definition: an extension's output belongs to the extension, because two extensions can share a task reference while applying different `Mapping`s.

- [ ] **Step 4: Tests pass; regression gate versus your own baseline.**

- [ ] **Step 5: Commit.**

---

### Task 3: Key extension output by extension, and read it back the same way

**Files:**
- Modify: `src/BBT.Workflow.Application/Extensions/Services/InstanceExtensionService.cs`
- Test: `test/BBT.Workflow.Application.Tests/Extensions/…` (create if absent)

- [ ] **Step 1: Write the failing tests.**

The contract, and the second one is the one that reproduces the Preprod fault:

1. **Two extensions, same task reference, different `Mapping`, different `Order`** (the silent path): each extension's `Response[extensionKey]` carries **its own** mapped output. Today the second overwrites the first and both read the same value.
2. **Two extensions, same task reference, same `Order`** (the parallel path): execution succeeds and each extension gets its own output. Today this throws `InvalidOperationException: Parallel tasks produced conflicting output for key '…'`. **Assert the absence of that throw explicitly**, so the test names the fault it fixes.
3. **`FindFailedExtensionKey` names the right extension** when two extensions share a task and one of them fails.
4. **One extension, one task** — unchanged behavior, so the fix does not disturb the common case.

- [ ] **Step 2: Run them, confirm they fail** — test 2 with the exact production exception message. Paste it.

- [ ] **Step 3: Implement.**

- Pass `ResponseVariableKey = extension.Key.ToVariableName()` per extension where the task list is built (`:212`, `var tasks = executableExtensions.Select(ext => ext.Task);` — this is where the extension identity is currently lost).
- `ExtractExtensionResponse` (`:280-286`) reads `variableKeyExtension` instead of `variableKeyTask`. `variableKeyTask` becomes unused — remove it rather than leaving it dead.
- `FindFailedExtensionKey` (`:262-266`) likewise keys on the extension.

- [ ] **Step 4: Tests pass; regression gate.**

- [ ] **Step 5: Commit.**

---

### Task 4: Make the duplicate-key warning hook-aware

**Files:** `TaskCoordinator.cs` (the `ResolveGroupEngineOptions` neighbourhood), `WorkflowLogs.cs`, and a test.

**Why:** the warning fired correctly on the Preprod fault and is what identified the cause — but its remedy text is wrong for the Extension hook. "Give the entries distinct orders" moves both writes onto the sequential path, where the second **silently overwrites** the first. It would hide the bug it just exposed.

- [ ] **Step 1: Write the failing test.** After Tasks 2-3, two extensions sharing a task have distinct response keys and are no longer a duplicate in any meaningful sense: assert the warning does **not** fire for the Extension hook in that shape, and still **does** fire for a genuine duplicate inside a transition hook.

- [ ] **Step 2: Run it, confirm it fails.**

- [ ] **Step 3: Implement.** Suppress or reword for the Extension hook. Read the current call site and message before deciding which — if the journal-level disambiguation still matters for extensions, keep the warning but change the remedy sentence; if it does not, do not fire it at all. **State which you chose and why in your report.**

- [ ] **Step 4: Tests pass; regression gate; commit.**

---

### Task 5: Document

**Files:** `docs/domain/` or `docs/runtime/` — find where extensions are already documented and extend that page rather than creating a new one; say which you chose.

- [ ] **Step 1:** Document that two extensions may share a task definition, that each applies its own `Mapping`, and that each extension's output is filed under its own key. Record the Preprod fault (trace `7873ad4e6c7a9db31c3b6401cd2c54fc`) as the reason, so the next reader knows this shape was once broken and how it presented.
- [ ] **Step 2:** If Task 1 found any consumer, add the `deprecations.json` entry here.
- [ ] **Step 3:** Commit.

---

## Self-Review

**Spec coverage:** criterion 1 → Task 3 test 1; criterion 2 (the silent sequential path) → Task 3 test 1 explicitly uses distinct orders, which is the path with no exception to catch it; criterion 3 → Task 3 test 3; criterion 4 (transition tasks unchanged) → Task 2 test with a null key, plus a Global Constraint the reviewer checks; criterion 5 → Task 4; criterion 6 → the regression gate on every task.

**Ordering:** Task 1 gates everything — it can stop the plan. Task 3 depends on Task 2's option existing. Task 4 depends on Tasks 2-3 having made the shape legal, or its assertion is meaningless.

**Known soft spots, stated rather than hidden:**
(a) Task 2 leaves the coordinator threading shape to the implementer between two named options, because choosing it well needs the real `ITaskCoordinatorExtended` and the internal grouping flow in front of you; the step requires the choice be reported and the "respect caller-set" constraint is fixed either way.
(b) `UpdateScriptContextWithResponse` is `static`, so the option has to reach it through its parameter list — the step says to check every caller rather than assuming there is one.
(c) Task 1 can end the plan. That is deliberate: the spec's accepted risk is conditional on the inventory, and discovering a consumer changes the remedy to parallel-key support plus a deprecation, which is a different piece of work.

**Not attempted:** the `ToVariableName` normalization collision (different separators/casing mapping to one name). It is real and it affects transition tasks too, so it is a separate decision — noted in the spec's Out of scope.
