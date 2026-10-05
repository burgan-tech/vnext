# Script Context and Engine

## Purpose

Scripts let workflow definitions express dynamic conditions, mappings, view rules, locks,
and small transformations without rebuilding the runtime. The runtime provides a bounded
`ScriptContext` and helper services so scripts can access approved workflow data and
platform utilities.

## Boundaries

The scripting module compiles and evaluates C# scripts. Pipeline/application code decides
where scripts are allowed. Scripts should receive context, not service locators for the
entire runtime. Side effects should remain explicit through approved helpers.

## Architecture Flow

1. Application code requests a script context from `TransitionExecutionContext`.
2. The context is built once and cached for the current transition.
3. Script evaluator compiles/runs C# using the scripting module.
4. Script can read headers, query parameters, instance data, state, transition, and
   approved helper services.
5. After execution, mutations are applied back to the live transition context when needed.
6. Finalize clears the script cache.

## Contracts

| Contract | Notes |
| --- | --- |
| `ScriptContext` | Runtime data passed into condition, mapping, and task scripts. |
| `ScriptBase` | Helper functions for secrets, logging, config, dynamic objects, XML, and collections. |
| `IScriptServices` | Provides Dapr client, logger, and configuration to approved helpers. |
| `CSharpEvaluator` | Compiles and evaluates script code. |
| `TransitionExecutionContext.Cache` | Holds script context for the current transition only. |

### Response slot: `variableKey`

Each task entry files its response under one slot in `context.TaskResponse` (and
`OutputResponse`): the entry's `variableKey` when authored, else `ToVariableName(task.key)`
(`send-notification` → `sendNotification`). Entries at the same `order` run in parallel and are
merged by slot, so two entries at one order must use distinct slots — the same task listed twice
(e.g. two SubProcess starts) needs a `variableKey` on at least one of them. Publish rejects a
same-order collision (`WorkflowValidator`, `FunctionComponentValidator`). A later order may reuse a
slot: its write overwrites the earlier value whether that order runs one entry or a parallel group.
The parallel merge (`ScriptContext.MergeParallelBranches`) compares only the slots the branches of
one group actually wrote, and throws "Parallel tasks produced conflicting output" only when two
branches of the same group write one slot with different payloads. An extension's
task entry files its response under the extension's own key; a `variableKey` there is ignored (the
extension key wins). The value is used verbatim: `context.TaskResponse["primaryChild"]`.

## Failure Modes

- Compilation failures should fail the calling task/condition with a clear error.
- Missing Dapr client or configuration throws from helper functions.
- Script mutations not applied back to transition context will not affect instance data.
- Long-running or side-effect-heavy scripts can make synchronous transitions slow.

## Pitfalls

### `dynamic` values inside anonymous types

`ScriptContext.Body` (and property accesses on it, e.g. `context.Body?.kkbScore`) is
`dynamic`. Feeding a `dynamic` value into an **anonymous type initializer** turns the
`new { … }` into a runtime (DLR) construction and leaves the anonymous type as an *open
generic*, which cannot be instantiated:

```csharp
// ❌ throws: Cannot create an instance of <>f__AnonymousType0`1[<creditBureau>j__TPar]
//    because Type.ContainsGenericParameters is true.
Data = new { creditBureau = new { kkbScore = result?.kkbScore } };
```

Fix it by keeping initializer values statically typed, or by not using anonymous types:

```csharp
// ✅ cast each dynamic leaf to a concrete type / object
Data = new { creditBureau = new { kkbScore = (object?)result?.kkbScore } };

// ✅ or build with the ScriptBase helpers (no anonymous types)
var creditBureau = CreateObject();
SetProperty(creditBureau, "kkbScore", result?.kkbScore);
var data = CreateObject();
SetProperty(data, "creditBureau", creditBureau);
Data = data;
```

When the runtime detects this specific failure it rewrites the error message with this
guidance (`ScriptDiagnostics.Explain`), so the surfaced error points at the fix instead of
the raw DLR text.

## Observability

Scripts can log through approved helper functions. Pipeline telemetry should identify the
transition and task/condition that triggered the script so compilation and execution
failures can be traced back to definition content.

## Change Safety

- Keep script helper surface small and explicit.
- Do not expose repositories or DbContext directly to scripts.
- Treat script context shape as a workflow-definition contract.
- Clear per-transition caches in finalize to avoid stale state across transitions.

## References

- `modules/BBT.Workflow.Modules.Scripting/README.md`
- `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs`
- `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Functions/ScriptBase.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs`
- `src/BBT.Workflow.Application/Tasks/Executors/Script/ScriptTaskExecutor.cs`

