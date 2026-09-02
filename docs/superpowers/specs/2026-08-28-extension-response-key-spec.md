# Spec: Extension Task Output Must Be Keyed by Extension, Not by Task

Date: 2026-08-28 · Status: approved for planning · Owner: platform
Repo: vnext (`/Users/U0B006/Documents/repos/burgan-tech/vnext`)
Trigger: Preprod fault, trace `7873ad4e6c7a9db31c3b6401cd2c54fc`, version `0.0.87-alpha.pr920.36`

## The ask

Two extensions on one workflow reference the same task. The runtime executes that task twice, both
executions write their result under the same script variable name, and the parallel merge rejects
the second write:

```
InvalidOperationException: Parallel tasks produced conflicting output for key 'scriptTaskSetTestViewData'
  at ScriptContext.MergeDictionary (Models.cs:862)
  at ScriptContext.MergeParallelBranch (Models.cs:828)
  at TaskCoordinator.ExecuteTaskGroupInParallelAsync (TaskCoordinator.cs:371)
```

Make two extensions able to share a task definition.

## Findings (verified in code, 2026-08-28)

### The collision

- `InstanceExtensionService.cs:212` builds the task list as `executableExtensions.Select(ext => ext.Task)`
  — one entry **per extension**. Two extensions referencing the same task therefore submit that task
  twice, at the same `Order`, in one coordinator call.
- `TaskCoordinator` runs a same-Order group in parallel, each task in a copy-on-write `ScriptContext`
  branch, then merges the branches in definition order (`TaskCoordinator.cs:371`).
- Every task writes its result under `taskKey.ToVariableName()` — `TaskResponse` for all tasks
  (`TaskExecutorBase.cs:352` → `Models.cs:631`) and additionally `OutputResponse` for extension
  tasks only (`TaskExecutorBase.cs:130-133`).
- `MergeDictionary` (`Models.cs:852-862`) throws when the same key arrives with a
  non-`JsonEquivalent` value. Both executions produce a value under `scriptTaskSetTestViewData`, so
  the merge throws.

### Why deduplication is the WRONG fix

The obvious shortcut — run a shared task once and give both extensions the result — is wrong, and
this is the decisive finding:

**`OnExecuteTask` carries `Mapping` (a `ScriptCode`) and `ErrorBoundary` per entry**
(`OnExecuteTask.cs:42,49`), while `Task` is only a `Reference`. Two extensions can therefore point
at the same task definition and apply **different output mappings**. Their outputs are *supposed* to
differ. Collapsing them would silently give one extension the other's transformed data.

So the values conflicting is correct behavior; the shared variable NAME is the defect.

### The defect is wider than the crash

Even without parallelism, extension output is looked up by task key:

- `ExtractExtensionResponse` (`InstanceExtensionService.cs:280-286`) computes both
  `variableKeyExtension = extension.Key.ToVariableName()` and
  `variableKeyTask = extension.Task.Task.Key.ToVariableName()`, then **reads
  `OutputResponse[variableKeyTask]`** and writes it to `Response[variableKeyExtension]`. Two
  extensions sharing a task both read the same slot, so they can never produce distinct output.
- `FindFailedExtensionKey` (`:262-266`) identifies which extension failed by which **task** output
  is missing — same flaw, so it can misattribute a failure to the wrong extension.

The sequential path writes with an indexer assignment (`TaskResponse[taskKey!] = value`), which
**silently overwrites**. So the parallel merge is the only thing that catches this at all: on a
sequential path the same definition produces wrong data with no error. The exception is luck, not
design.

### What the current warning tells the author is wrong for this hook

`DuplicateTaskKeyAtSameOrder` fired correctly and identified the cause, but its remedy text —
"give the entries distinct orders if they are meant to run as separate steps" — is wrong for the
Extension hook. Distinct orders would move both writes onto the sequential path, where the second
silently overwrites the first. The warning would hide the bug it just exposed.

### Blast radius of changing the key

- `OutputResponse` is written **only** for `TaskTrigger.Extension` (`TaskExecutorBase.cs:130`), so it
  is internal plumbing between the executor and `ExtractExtensionResponse`, not a general surface.
- `TaskResponse` is written for **every** task and is a `public` dictionary on `ScriptContext`, so an
  authored `.csx` could read it by task-variable name. No `.csx` in vnext-example does
  (they read `context.Body`), but a domain package might.
- The outward extension surface — `Response[extensionKey]` — does **not** change under this fix.

## Decisions taken

- **Extension task output is keyed by the EXTENSION, not the task.** `ExtractExtensionResponse` and
  `FindFailedExtensionKey` then read the key they already compute for the extension, and two
  extensions sharing a task each get their own slot.
- **The key is threaded through `TaskEngineExecutionOptions`**, as a new `ResponseVariableKey`
  mirroring the existing `JournalTaskKey`: null means today's behavior (task key), a caller-set value
  wins. That record is already the established seam for per-task disambiguation, and
  `ResolveGroupEngineOptions` already implements "respect a value the caller set"
  (`TaskCoordinator.cs:479-486`).
- **Only the extension path sets it.** Transition-scoped tasks (`onExecute`, `onEntry`, `onExit`)
  keep the task-keyed variable exactly as today, so no authored script that reads
  `TaskResponse["someTask"]` for a transition task is affected.
- **`TaskResponse` for extension tasks is keyed by extension too**, not only `OutputResponse`. Keying
  one and not the other would leave the crash in place, since the throw comes from the `TaskResponse`
  merge (`Models.cs:828`).
- **The `DuplicateTaskKeyAtSameOrder` warning becomes hook-aware.** For the Extension hook the
  "distinct orders" advice is removed; sharing a task becomes legal and the warning should not fire
  for it at all once the keys are distinct.
- **No deduplication of shared tasks.** Two extensions sharing a task run it twice, by design — their
  mappings differ.

## Accepted risks

- An authored script that reads an EXTENSION task's `TaskResponse["<taskVariable>"]` would stop
  finding it under that name. This is judged acceptable but must be verified rather than assumed:
  the plan includes an inventory step across the example repo and the docs before the change lands,
  and a `deprecations.json` entry if any consumer surface is found.

## Out of scope

- The `ToVariableName` collision class in general (`script-task-x`, `script_task_x` and
  `Script-Task-X` all normalize to one name). Real, but a separate decision — it affects transition
  tasks too and cannot be fixed without either a breaking rename or a validator.
- Parallel-merge semantics, the copy-on-write branch model, and `JsonEquivalent`.
- The duplicate-task-key case inside a single transition hook, which the existing per-occurrence
  `JournalTaskKey` already disambiguates at the journal level.

## Success criteria

1. A workflow with two extensions referencing the same task, with different mappings, executes and
   each extension's `Response[extensionKey]` carries **its own** mapped output.
2. The same definition on a sequential path (distinct orders) also produces distinct outputs — the
   silent-overwrite path is closed, not just the throwing one.
3. `FindFailedExtensionKey` names the extension that actually failed when two extensions share a task.
4. Transition-scoped tasks are unchanged: their `TaskResponse` / journal keys are byte-identical.
5. `DuplicateTaskKeyAtSameOrder` no longer fires for two extensions sharing a task, and its remedy
   text no longer advises distinct orders for the Extension hook.
6. No new failing test name in `Application.Tests` or `Domain.Tests`.
