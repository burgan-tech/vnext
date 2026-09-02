# Extensions

## Purpose

An **Extension** enriches instance data on a read without the client requesting it explicitly —
`InstanceExtensionService` runs a set of Extension components (core, workflow-level) alongside a
State/Data/Master read and merges their results into the response. Each `Extension` component
carries a `Task` (a `Reference` to a task definition) plus its own `Mapping` (an inline C# script)
and optional `ErrorBoundary`.

## Two extensions may legitimately share one task definition

`Extension.Task` is only a **reference** to a task, not a private copy of it. Nothing in
`WorkflowValidator` (or any other component validator) requires task references across different
Extensions to be distinct, and there is a real reason to point two Extensions at the same task: the
task does the I/O (e.g. one HTTP call to a shared lookup service), while each Extension's own
`Mapping` decides what to do with the result and each Extension's own `ErrorBoundary` decides how to
handle a failure. `OnExecuteTask` — the type that pairs a task reference with its execution
metadata — carries `Mapping` and `ErrorBoundary` **per entry**; `Task` is the only field that is
shared. Two Extensions built from two `OnExecuteTask` entries pointing at the same task Reference
are therefore expected to produce **different** outputs, because they apply different mappings to
the same raw result — not a coincidence to guard against, an intentional composition.

`InstanceExtensionService.ExecuteExtensionsInternalAsync` keys its per-task response override by the
`OnExecuteTask` instance itself (not by task key), specifically so this composition works:

```csharp
// Two extensions can share a task Reference while applying different Mapping/Order —
// their outputs are supposed to differ (OnExecuteTask carries Mapping/ErrorBoundary per
// entry; Task is only a Reference).
var responseKeyByTask = new Dictionary<OnExecuteTask, string>();
foreach (var ext in executableExtensions)
{
    responseKeyByTask[ext.Task] = ext.Key.ToVariableName();
}
```

## Each extension's output is filed under its own key

Execution runs through `TaskCoordinator.ExecuteWithDetailsAsync` with `TaskTrigger.Extension` /
`TaskExecutionOrigin.Extension`, using the `optionsRefiner` overload to set
`TaskEngineExecutionOptions.ResponseVariableKey` to the **extension's** own key
(`extension.Key.ToVariableName()`) for every task it runs — not the task's key. That option value
flows down to `TaskExecutorBase.UpdateScriptContextWithResponse`, which writes both
`ScriptContext.TaskResponse[variableKey]` and (for `TaskTrigger.Extension`)
`ScriptContext.OutputResponse[variableKey]` under `responseVariableKey ?? taskKey.ToVariableName()`.
With the override set, `variableKey` is always the extension's key, regardless of how many other
extensions share the same underlying task. `ExtractExtensionResponse` then reads each extension's
result back out by that same extension key, never by the task's.

This also means the journal-key disambiguation `TaskCoordinator.ResolveGroupEngineOptions` performs
for a same-key/same-order group (`JournalTaskKey` "#0"/"#1" suffixing) has nothing to do for the
Extension hook: `ExtensionTaskPersistenceStrategy` never persists an `InstanceTask` row for
`TaskExecutionOrigin.Extension` executions, so there is no journal entry for that suffixing to keep
distinct. It matters only for transition hooks (`onExecute`/`onEntry`/`onExit`), where task
executions are journaled. Because of this, `TaskCoordinator`'s duplicate-task-key-at-same-order
warning (`WorkflowLogs.DuplicateTaskKeyAtSameOrder`, EventId 10155) — whose remedy is "give the
entries distinct orders" — is suppressed for `TaskExecutionOrigin.Extension` executions: for a
transition hook a repeated key is almost certainly a copy-paste mistake, but for the Extension
origin it is the supported shape described above, and the remedy would be advice with no problem to
fix.

The suppression is keyed on `TaskExecutionOrigin.Extension`, deliberately **not**
`TaskTrigger.Extension` — custom functions (`FunctionAppService.cs`) execute through the same
`TaskTrigger.Extension` trigger but with `TaskExecutionOrigin.Function`, and a multi-task function
(`FunctionAppService.GetSingleTaskVariableKey` exists precisely to distinguish single-task from
multi-task functions) has no per-entry response-key override to save it from a duplicated task key
at the same order — that shape is still a plain authoring mistake and the warning still fires for
it.

A second, distinct shape is **not** suppressed and should not be confused with the one above: the
SAME extension reference listed twice in a workflow's `Extensions` (see "Two extensions may
legitimately share one task definition" — `WorkflowValidator` has no uniqueness check on
`Extensions`). That collapses onto the SAME `OnExecuteTask` instance, not two different ones, and
`InstanceExtensionService`'s last-wins `responseKeyByTask` build detects it — logged as
`WorkflowLogs.DuplicateExtensionReference` (EventId 20102) with the remedy "remove the duplicate
reference" (distinct orders would not help; the sequential path overwrites silently regardless of
order at `ScriptContext.SetOutputResponse`).

## Why this is written down: the Preprod fault

Before this shape was fixed, both writes above were keyed by the **task's** key
(`taskKey.ToVariableName()`), not the extension's. Two Extensions sharing one task Reference at the
same order collapsed onto the same variable name:

- At the **same order**, both executions ran in the parallel branch and the merge threw
  `InvalidOperationException: "Parallel tasks produced conflicting output for key '...'"` — because
  the two branches wrote genuinely different values (their own `Mapping` output) under what the
  merge saw as one shared key.
- At **different orders**, the second execution would have silently overwritten the first's entry
  in `ScriptContext.TaskResponse`/`OutputResponse` — the same collision without the crash to reveal
  it.

This fired in Preprod (trace `7873ad4e6c7a9db31c3b6401cd2c54fc`): a workflow with two extensions
referencing the same task crashed the parallel merge. The fix (this page documents its result) is
the per-extension `ResponseVariableKey` override described above — each extension now owns its own
output slot regardless of which task it shares. This section exists so the next reader who
encounters two extensions pointing at the same task does not mistake it for a bug: it is how the
shape was once broken, and it is what "fixed" looks like.

## Consumer impact

No consumer reads an Extension task's `TaskResponse`/`OutputResponse` by the task-derived key — the
fix's key-injection is opt-in (`ResponseVariableKey`, set only by `InstanceExtensionService`), and a
full inventory of `.csx` mappings, this repo's docs, the published `vnext-docs`, and in-repo C# reads
found no such consumer (see `docs/runtime/extension-response-key-inventory.md`). No deprecation was
needed for this change.
