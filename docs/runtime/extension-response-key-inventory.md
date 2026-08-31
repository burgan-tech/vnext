# Extension Response Key — Consumer Inventory

Date: 2026-08-28 · Task 1 of `docs/superpowers/plans/2026-08-28-extension-response-key.md`
Spec: `docs/superpowers/specs/2026-08-28-extension-response-key-spec.md`

## Question

The planned fix (Task 2-3 of the implementation plan) files an **Extension** task's output —
`TaskResponse[taskKey]` and `OutputResponse[taskKey]` — under the **extension's** key instead of
the task's, for tasks executed by `InstanceExtensionService` (`TaskExecutionOrigin.Extension`).
Transition-scoped tasks (`onExecute`/`onEntry`/`onExit`) and Function tasks are unaffected because
the mechanism is opt-in (`TaskEngineExecutionOptions.ResponseVariableKey`, set only by the extension
service's call site).

Does anything read an **Extension** task's `TaskResponse`/`OutputResponse` by the task-derived
variable name (`taskKey.ToVariableName()`)? If yes, name it — that changes the remedy to parallel
key support + a `deprecations.json` entry.

## Searches run and results

### 1. `.csx` scripts in vnext-example — indexer access

```
cd /Users/U0B006/Documents/repos/burgan-tech/vnext-example
grep -rn "TaskResponse\[" --include="*.csx" .
grep -rn "OutputResponse\[" --include="*.csx" .
grep -rln "TaskResponse" --include="*.csx" .
grep -rln "OutputResponse" --include="*.csx" .
```

Result: zero matches for indexer access (`TaskResponse[...]` / `OutputResponse[...]`). Zero matches
for `OutputResponse` at all, anywhere. Seven files mention the string `TaskResponse`, but every
occurrence is inside a comment referring to `StandardTaskResponse` (the response *envelope shape*,
unrelated to the `ScriptContext.TaskResponse` dictionary) — e.g. `CollateralSubFlowMapping.csx:13`,
`ReleaseBlockMapping.csx:11`, `InquireFindeksMapping.csx:39`. None of the seven read the dictionary.

### 2. Actual Extension component `.csx` mappings in vnext-example

```
find . -path "*/Extensions/*" -iname "*.csx" -not -path "*/dist/*"
```

Found two: `core/Extensions/account-opening/src/UserSessionMapping.csx` and
`core/Extensions/future-pay/customer-profile-enrichment/src/CustomerProfileEnrichmentMapping.csx`.
Read both in full. Both `OutputHandler`s build their `ScriptResponse.Data` from `context.Body` /
`context.Headers` only. Neither touches `context.TaskResponse` or `context.OutputResponse`.

### 3. `docs/` and `ai-docs/` in this repo

```
grep -rn "TaskResponse\|OutputResponse" docs/ --include="*.md" | grep -v "docs/superpowers/"
grep -rn "TaskResponse\|OutputResponse" ai-docs/ 2>/dev/null
```

- `docs/runtime/get-instance-task.md:80` — `var response = context.GetTaskResponse("load-account");`.
  Verified this method **does not exist**: `grep -rn "GetTaskResponse" . --include="*.cs" --include="*.csx" --include="*.md"`
  returns only this one doc line, and `Models.cs` (the `ScriptContext` source) has no such member —
  only `SetStandardResponse`, `SetOutputResponse`, and a test-only `Builder.SetTaskResponse`. This is
  a **stale/broken doc example** (does not compile against the real API), not a working consumer. Not
  a blocker for this fix, but worth a separate cleanup — flagging, not fixing here (out of scope for
  Task 1: "Files: none modified").
- `docs/domain/fan-out-task.md:316` — mentions the shared `TaskResponse` dictionary in the context of
  why Fan-Out deliberately does **not** use `MergeParallelBranch` (duplicate-key collision risk). Not
  a consumer, an architecture note about the same collision class this fix addresses elsewhere.
- All other `docs/` hits are under `docs/superpowers/` (this feature's own plan/spec/scratch docs and
  unrelated internal planning docs) — internal SDD artifacts, not author-facing documentation.
- `ai-docs/script-perf-analysis-2026-08-23.md:45` — a performance note about `CreateParallelBranch`
  JSON-round-tripping the dictionaries; not a read by key.

Verdict for this repo's docs: **no author-facing documentation teaches indexer access to
`TaskResponse`/`OutputResponse` for Extension tasks.** One broken example exists (`GetTaskResponse`)
but it doesn't compile today regardless, and it's on a Function-scoped page (`GetInstanceTask` used
as a Function task, see finding 5).

### 4. Every in-repo C# read of `OutputResponse`/`TaskResponse` outside `ScriptContext`

```
grep -rn "\.TaskResponse\b\|\.OutputResponse\b" --include="*.cs" src orchestration execution workers modules test | grep -v "/Scripting/Models.cs"
```

Production reads (non-test):

| File:Line | Reads by | Task's `TaskExecutionOrigin` | Affected by this fix? |
|---|---|---|---|
| `InstanceExtensionService.cs:265,283` (`ExtractExtensionResponse`) | task key (`variableKeyTask`) | `Extension` | **Yes — this is the code the fix changes** (Task 3 of the plan rewrites it to read by extension key). |
| `FunctionAppService.cs:489` (`ExtractRawFunctionResponse`) | task key (`variableKeyTask` = single task's key) | `Function` | No — see finding 5. |
| `FunctionAppService.cs:533` (`ExtractFunctionResponse`) | task key | `Function` | No — see finding 5. |
| `FunctionAppService.cs:588` (`ExtractSingleTaskHttpMetadata`) | task key, reads `TaskResponse` | `Function` | No — see finding 5. |

Test-only reads (`test/BBT.Workflow.Application.Tests`, `test/BBT.Workflow.Domain.Tests`): all key by
task name in ways consistent with today's default (null `ResponseVariableKey`) behavior; none assert
on an *extension's* task being read back by task key, so none of these pin the risky behavior.

### 5. Important finding: Functions share `TaskTrigger.Extension`, but not the fix's call site

`OutputResponse` is populated whenever `context.TaskTrigger == TaskTrigger.Extension`
(`TaskExecutorBase.cs:130`). That trigger value is used by **two** different callers, distinguished
only by the separate `TaskExecutionOrigin` enum:

- `InstanceExtensionService.ExecuteExtensionsInternalAsync` — `TaskTrigger.Extension` +
  `TaskExecutionOrigin.Extension` (real Extension components). **This is the call site Task 3 of the
  plan modifies.**
- `FunctionAppService.<ExecuteAsync>` (~line 270-278) — `TaskTrigger.Extension` +
  `TaskExecutionOrigin.Function` (a Function's own tasks, single- or multi-task). **This call site is
  not touched by the plan.**

`FunctionAppService` reads `OutputResponse`/`TaskResponse` by **task** key
(`GetSingleTaskVariableKey(function)` → the function's single execute task's `Key.ToVariableName()`)
in three places (table above), and this pattern is **explicitly documented for domain authors**:

- `vnext-docs/docs/components/functions/custom.md:160,212-213` —
  ```csharp
  var policies = context.OutputResponse["validateAccountPolicies"].data;
  var instanceData = context.OutputResponse?["getDataFromWorkflow"].data;
  ```
- `vnext-docs/docs/components/interfaces.md:88-105` — `IOutputHandler` is explicitly scoped to
  **Function** components ("Bir **Function** bileşeninin `output` alanına bağlanan..."; the
  `IMapping` vs `IOutputHandler` comparison table lists "Bağlandığı yer: Function" for
  `IOutputHandler` vs "Task" for `IMapping`).
- `vnext-ai-toolkit/references/concepts/csx-contracts.md:89-105`,
  `vnext-ai-toolkit/references/function-mapping-pattern.md:44-47`,
  `vnext-ai-toolkit/references/concepts/mapping-types.md:55-56` — same pattern, same Function scoping
  ("in a multi-task Function's final `IOutputHandler`").

This is **not a breaking consumer of the planned fix**, because:
1. The fix's key-injection is opt-in per call (`TaskEngineExecutionOptions.ResponseVariableKey`,
   default `null` = today's `taskKey.ToVariableName()` behavior), not a global switch keyed off
   `TaskTrigger.Extension`.
2. Per the plan (`docs/superpowers/plans/2026-08-28-extension-response-key.md` Architecture section
   and Task 3 scope), **only** `InstanceExtensionService`'s call sets `ResponseVariableKey`.
   `FunctionAppService`'s call is never modified, so its tasks keep `ResponseVariableKey = null` and
   continue to be keyed by task name exactly as documented above.

It **is** a landmine for the Task 2 implementer, worth calling out explicitly: `TaskExecutorBase.cs`
gates writing `OutputResponse` at all on `context.TaskTrigger == TaskTrigger.Extension` — a value
shared by Functions and Extensions. If Task 2/3 is implemented by branching on `TaskTrigger` (or on
"is `OutputResponse` populated") rather than strictly on a caller-supplied `ResponseVariableKey` being
non-null, it will silently break `FunctionAppService`'s three task-keyed reads above, which are real,
documented, production code paths. The plan's own design (opt-in field, only one caller sets it) is
correct and avoids this — flagging it here as a check for the Task 2/3 code review, not as a defect
in the plan as written.

### 6. `vnext-docs` — published product documentation

```
grep -rln "TaskResponse\|OutputResponse" /Users/U0B006/Documents/repos/burgan-tech/vnext-docs
```

Hits: `docs/components/mappings.md`, `docs/components/interfaces.md`, `docs/components/tasks/fan-out.md`,
`docs/components/functions/custom.md` (+ EN i18n mirrors), plus internal
`docs/superpowers/plans/2026-04-24-...` and a blog migration note (not author guidance).

- `mappings.md:313-319` and `interfaces.md:598` document `context.TaskResponse["httpTask"]` /
  `context.TaskResponse["scriptTask"]` generically, in the context of `IMapping.OutputHandler`
  (per-task, **transition-scoped** usage — task's own `OutputHandler` reading its own or a prior
  task's result within the same transition). This describes `TaskTrigger.OnExecute`/`OnEntry`/`OnExit`
  usage, which the plan explicitly keeps untouched (`ResponseVariableKey` stays null there).
- `functions/custom.md` and `interfaces.md` (IOutputHandler section) — Function-scoped, see finding 5.
- `tasks/fan-out.md:453` — architecture note about the same dictionary-collision class Fan-Out avoids
  by design; not a read by key.
- No hit in `docs/components/extension.md` (164 lines, checked in full for `OutputHandler`/`context.`/
  `Response[` mentions) or `docs/getting-started/tutorial-views-extensions.md`. Extension component
  documentation does not teach `TaskResponse`/`OutputResponse` indexer access at all — its examples
  build `ScriptResponse.Data` from `context.Body`, matching what the two real Extension `.csx`
  mappings in vnext-example actually do (finding 2).

### 7. `.claude/rules/` in this repo

```
grep -rn "TaskResponse\|OutputResponse" /Users/U0B006/Documents/repos/burgan-tech/vnext/.claude/rules/
```

Zero matches. Not mentioned.

## Verdict

**SAFE.** No `.csx` script, no author-facing documentation (this repo's `docs/`/`ai-docs/`, the
published `vnext-docs`, or the `vnext-ai-toolkit` scaffolding references), and no in-repo C# code
reads an **Extension** task's `TaskResponse`/`OutputResponse` by the task-derived variable name. The
one real production consumer that reads these dictionaries by task key —
`FunctionAppService`'s three extraction methods — belongs to `TaskExecutionOrigin.Function`, whose
call site is not modified by the planned fix (the fix is opt-in via `ResponseVariableKey`, set only
by `InstanceExtensionService`), and its documented usage (`vnext-docs/docs/components/functions/custom.md`,
`interfaces.md`, `vnext-ai-toolkit` csx-contracts) remains accurate unchanged.

One documentation defect found in passing, unrelated to this fix's safety: `docs/runtime/get-instance-task.md:80`
references a `context.GetTaskResponse(...)` method that does not exist anywhere in the codebase — a
stale/broken example. Not fixed here (Task 1 modifies no source files); worth a follow-up.

**Caution for Task 2/3 implementation:** keep the key-injection strictly opt-in
(`ResponseVariableKey` non-null, set only by `InstanceExtensionService`) rather than branching on
`TaskTrigger.Extension` or "is `OutputResponse` populated" — that trigger value is shared with
Functions, whose task-keyed reads are real and documented (finding 5).
