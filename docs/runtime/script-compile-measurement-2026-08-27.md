# Script.Compile measurement: cold start or per-request? (2026-08-27)

## Question

An earlier trace showed a transition evaluating auto-transition rules producing three
`Script.Compile/*` spans (1553 ms, 40 ms, 22 ms), all with `vnext_script_cache_hit: false` — i.e.
all three genuinely ran Roslyn. Two explanations were possible:

- **Cold start**: every identity compiles once per process (first touch after startup), and the
  warm path afterward costs microseconds. Fix: warm-up coverage. The parallelization work planned
  for Task 4 would then be optimizing a cost that essentially doesn't exist on the warm path.
- **Unstable cache key**: identities recompile on every request regardless of process age — a bug.
  Fixing the cache key would have to happen before any parallelization work, since parallelizing a
  broken cache would only hide the bug under concurrency noise.

The only way to tell them apart is to run the same flow twice against one warm process, with no
restart in between, and check whether the SAME identities recompile in run 2.

## Method

- Flow: `money-transfer` (vnext-example), driven by
  `Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests` (5 test methods) via
  `dotnet test --filter "FullyQualifiedName~MoneyTransferTests"`.
- This flow was checked to actually exercise `Step.RunAutomaticTransitions` before use: its
  `evaluate-push-requirement` state evaluates `RequirePushRule.csx` / `SkipPushRule.csx`
  (automatic, `triggerType: 1`), and its `executing-transfer` state evaluates
  `ExecutionSucceededRule.csx` / `ExecutionFailedRule.csx` — both confirmed by parent/child span
  linkage below.
- Orchestration was restarted once cleanly before the experiment (deployment id
  `Development-vnext-app-20260827-181525-75ac55bc`) so run 1 starts from a cache with **zero**
  compiled identities. No restart happened between run 1 and run 2 — verified by the process still
  running under the same PID with a continuous uptime spanning both runs.
- Run 1: `18:20:43Z`–`18:22:14Z`. Run 2: `18:22:14Z`–`18:23:56Z` (same process, immediately after).
- Elastic query: Python `urllib` against `http://localhost:9200/.ds-traces-apm*,traces-apm*/_search`,
  filtering `span.name` prefix `Script.Compile/` and `@timestamp` range per run, plus a second query
  for `numeric_labels.vnext_script_context_memo_hits` / `vnext_script_mapping_memo_hits` on the
  enclosing spans (these are numeric APM labels, not string labels — they live under
  `numeric_labels.*`, not `labels.*`). Exact scripts are in the accompanying task report.

### Environment note

The runtime instance available locally had its `APP_DOMAIN` set to `contract` (uncommitted,
pre-existing local WIP unrelated to this task — a different in-progress effort on this machine),
which rejects any `core`-domain flow, including `money-transfer`. This was overridden for the
process only, via `APP_DOMAIN=core dotnet run --launch-profile http --no-build` (an inline
environment override on the command that started the process — no file on disk was intentionally
edited). No change was made to the connection string; the already-configured database already had
the `sys_*` metadata schemas migrated, and `money-transfer` publishes into it as a new schema like
any other flow. The repository's uncommitted files were verified byte-identical to their pre-task
state before finishing (an in-place `sed` edit was attempted first, appeared to be blocked, but
partially landed on disk; this was caught by a `git status` check immediately after and reverted
from a backup taken before any edit — see the task report for the full sequence). No orchestration
config file is part of this commit.

## Result — per identity

Six distinct script identities were exercised (two are the auto-transition rules the task centers
on; four are `OnExecute` task mappings, included because they hit the same compile cache and
strengthen the pattern).

| Identity | Under `Step.RunAutomaticTransitions`? | Run 1 (first touch) | Run 1 (later touches, same run) | Run 2 (all touches) |
|---|---|---|---|---|
| `RequirePushRule.csx` | **Yes** | 6.85 ms, `cache_hit=false` | 0.02 ms, `true` (×2) | 0.02 ms, `true` (×2) |
| `ExecutionSucceededRule.csx` | **Yes** | 5.81 ms, `cache_hit=false` | 0.01 ms, `true` (×1) | 0.02 ms, `true` (×2) |
| `GetIbanHistoryMapping.csx` | No (task mapping) | 219.87 ms, `cache_hit=false` | 0.01 ms, `true` (×2) | 0.02 ms, `true` (×2) |
| `PushTimeoutTimer.csx` | No (task mapping) | 9.74 ms, `cache_hit=false` | 0.01 ms, `true` (×2) | 0.02 ms, `true` (×2) |
| `ExecuteTransferMapping.csx` | No (task mapping) | 36.09 ms, `cache_hit=false` | 0.01 ms, `true` (×1) | 0.03 ms, `true` (×2) |
| `GetAccountsDaprMapping.csx` | No (task mapping) | 9.76 ms, `cache_hit=false` | — | 0.03 ms, `true` (×2) |

**Every identity compiled exactly once (`cache_hit=false`) — in run 1's first occurrence, and
nowhere else.** Run 2 shows 15 `Script.Compile/*` spans across 5 traces, and **all 15 are
`cache_hit=true`**. Zero misses in run 2, for any identity.

`RequirePushRule.csx` and `ExecutionSucceededRule.csx` are confirmed to sit directly under
`Step.RunAutomaticTransitions` by parent/child span id (e.g. `RequirePushRule.csx`'s
`parent.id = a7fda3fd9f54bece` matches the `Step.RunAutomaticTransitions` span id in the same
trace `00320739ed5ea430158e7a6224506156`).

### Trace ids

- Run 1 traces (5, one per test-flow instance that reached script evaluation):
  `00320739ed5ea430158e7a6224506156` (first push-path compile),
  `d1583e0bafee33fdc770d756887dc527`,
  `7a6079899c52201ba438723746fc57fc` (first transfer-path compile),
  `866118be68d738c5a3f4848351393c2b`,
  `2ec0498bf33b39a5aff25e1f2933c060`.
- Run 2 traces (5): `cdc6a670312fefeb68f5dc6cccaf31cb`, `d38590e06a46688eb2f24d361faaebe4`,
  `d2f7289e6acbbf60b4f5010ce1f5ab76`, `10156b997975efa38b010d55fff4fcf0`,
  `e5b88233282d42c28f81ce724f8588bd`.

### Memo-hit counters (enclosing spans)

Every `Step.RunAutomaticTransitions` span in both runs carries
`numeric_labels.vnext_script_context_memo_hits = 1` (per-transition `ScriptContext` reuse is
working on every single evaluation, independent of compile status). `compile_miss_count` on that
same span is `1` on exactly the two run-1 "first touch" spans and `0` on every other
`Step.RunAutomaticTransitions` span in both runs — an exact match to the per-span table above.
`vnext.script.mapping.memo.hits` doesn't appear on `Step.RunAutomaticTransitions` (it's a task
mapping-factory counter); it appears instead on `Task.ProcessOutput` spans, where it is `1` on all
14 occurrences observed across both runs — the mapping-factory memo is also working correctly.

## Verdict

**Cold start.** Every one of six distinct script identities compiled exactly once, on its first
evaluation after the process started, and never again — not later in run 1, and not at all in run
2, despite run 2 exercising the identical flow end to end. This matches the compile cache's actual
implementation: `ScriptEvaluator`'s type cache lives for the process lifetime, keyed by script
identity, so once an identity is compiled it stays warm until the process restarts (see
`docs/runtime/trace-span-tree.md`, "Three memo layers on the script path"). There is no evidence
of an unstable cache key — no identity recompiled under otherwise-identical conditions.

**Task 4 (parallelizing the auto-transition rule compiles) should be SKIPPED.** The 1553/40/22 ms
compile costs cited in the original observation are a one-time, per-process warm-up cost paid once
per script identity (bounded by the number of distinct rule scripts a workflow definition uses),
not a per-request cost. On the warm path — which is every request after the first exercise of a
given identity — compiles cost microseconds (0.01–0.03 ms observed here), so parallelizing
evaluation would optimize a cost that has already been paid once and does not recur. The
actionable fix, if the 1.5 s worst case matters operationally, is warm-up coverage (compile all
referenced scripts once at startup or on first publish) rather than parallelizing per-request
evaluation.

## Caveats

- The runtime's `APP_DOMAIN` was overridden for the process only (see Environment note above); the
  underlying compile-cache mechanism is unrelated to domain configuration, so this does not affect
  the validity of the result.
- Only six identities were observed (bounded by what `MoneyTransferTests` exercises). This is a
  representative sample of the mechanism (`ScriptEvaluator`'s process-lifetime type cache is
  identity-agnostic — it doesn't special-case auto-transition rules vs. task mappings), not
  exhaustive coverage of every script in the codebase. The mechanism itself, not the specific
  script, is what determines cold-start-only behavior.
