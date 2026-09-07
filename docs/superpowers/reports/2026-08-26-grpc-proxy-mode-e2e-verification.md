# gRPC proxy-mode transport + DaprServiceTask — end-to-end verification report

Date: 2026-08-26
Repo under test: `vnext-example` (branch `feature/caller-role-provider`), against locally-built
`vnext` runtime (branch `feature/trace-span-tree`).

## Summary

- gRPC proxy-mode orchestration→execution invocation: **verified working**, with trace continuity
  across the hop (same `trace.id` on both sides, `parent.id` chained through the Dapr sidecar spans).
- DaprServiceTask reached through the gRPC hop: **verified working** — `get-accounts-dapr` executed
  successfully and produced spans in both the gRPC run and the HTTP A/B run.
- `MoneyTransferTests`: **5/5 passed on both transports**, but only after three additional,
  pre-existing, transport-independent bugs in `vnext-example` were found and fixed (details below).
  Do not read the 5/5 as "nothing needed fixing" — the honest sequence is documented in full.

## vnext-example changes

Commits on `feature/caller-role-provider`:

| SHA | Summary |
|---|---|
| `08ab6e2` | feat(money-transfer): add DaprService transport-probe task alongside execute-transfer |
| `0d01306` | fix(money-transfer): correct get-iban-history's filter/sort wire format |
| `642dc5a` | fix(money-transfer): bump version to 1.1.1 to actually publish the iban-history fix |
| `e475dcb` | fix(money-transfer): mark targetIban filterable so get-iban-history can query it |

Files touched:
- `core/Tasks/money-transfer/get-accounts-dapr.json` (new) — DaprService (type `3`) task, `appId:
  mocklab`, `methodName: api/payments/accounts`, `httpVerb: GET`. Same MockLab endpoint as the
  existing HTTP task `get-accounts.json`, chosen so the two are a deliberate A/B.
- `core/Workflows/money-transfer/src/GetAccountsDaprMapping.csx` (new) — minimal `IMapping`; nothing
  dynamic to shape since the task config is static, so `InputHandler` is a no-op cast-check and
  `OutputHandler` reports success/failure by status code only (no `Data` merged into instance data,
  to stay clear of schema risk — this is a transport probe, not a business feature).
- `core/Workflows/money-transfer/money-transfer.json` — added the new task as `order: 2` in the
  `executing-transfer` state's `onEntries`, alongside the existing `execute-transfer` (order 1), so
  one transition (`approve-push`, which drives `awaiting-push-approval` → `executing-transfer`)
  runs both an HTTP task and a DaprService task and both show up in the same
  `TransitionJob.Execute/approve-push` trace. Nothing existing was removed or reordered. Workflow
  version bumped `1.0.0 → 1.1.0 → 1.1.1 → 1.1.2` across the four commits (see "Unexpected findings"
  for why three bumps were needed instead of one).
- `core/Workflows/money-transfer/src/GetIbanHistoryMapping.csx` and
  `core/Schemas/money-transfer/money-transfer-master.json` — bugfixes to a **pre-existing, unrelated**
  task, required only to get any instance past the `confirm` transition at all. See below.

`npm run validate`: all money-transfer files pass both before and after. The only failures in the
domain are 12 pre-existing `fan-out-config-matrix` files unrelated to this work (matches the known
issue in memory: FanOutTask schema release pending, vnext-schema stuck on old task-type range).

## Unexpected findings (pre-existing, transport-independent bugs)

While driving `MoneyTransferTests` to reach `executing-transfer` (needed to observe the new task at
all), every instance faulted at the `confirm` transition, **before** `get-accounts-dapr` or
`execute-transfer` ever ran. This blocked the entire verification goal, so it had to be resolved.
Confirmed by code trace, not guesswork, that neither issue below is transport-related:
`GetInstancesTaskExecutor.InvokeAsync` (orchestration/BBT.Workflow.Application) validates
`task.Filter`/`task.Sort` via `InstanceQueryValidator` synchronously, in-process, **before** the
same-domain/cross-domain dispatch branch — i.e. before any local-vs-remote or HTTP-vs-gRPC decision
is made.

1. **Wrong `SetFilter` overload.** `GetIbanHistoryMapping.InputHandler` called
   `getInstancesTask.SetFilter(new[] { "data.targetIban==..." })` — a `string[]`, which resolves to
   the `SetFilter(object?)` overload (serializes to a JSON *array*) rather than `SetFilter(string?)`.
   `InstanceQueryValidator` rejected it: `"Expected start of object for GraphQLFilterNode"`. The
   task's static config also carried a legacy `"sort": "-CreatedAt"` string, not JSON, rejected the
   same way. Fixed by rebuilding the query with the fluent `InstanceQuery` + `SetFilterSpec` (per
   `docs/runtime/instance-filtering-and-queries.md`):
   `InstanceQuery.Create().Where("attributes.targetIban", f => f.Eq(targetIban)).OrderByDescending("createdAt").Build()`.

2. **`targetIban` not filterable.** Once (1) was fixed and republished, the query got past JSON
   validation but was rejected one layer deeper: `"Field 'targetIban' is not filterable."`
   `money-transfer-master.json` never declared `x-filterOperators` on `targetIban` — every other
   queryable field in the domain (e.g. `core/Schemas/otp-auth/otp-auth-master.json`) has this
   annotation; `targetIban` simply never got it when `get-iban-history` was built. Fixed by adding
   `"x-filterOperators": ["eq"]`.

3. **Version-immutability trap, twice.** Fix (1) was committed without bumping `money-transfer`'s own
   version past `1.1.0` (already published from the transport-probe commit). Re-publishing `1.1.0`
   was a silent no-op — the runtime kept serving the old, broken mapping, and the second gRPC test
   run failed identically to the first with zero indication the fix had even been attempted. Caught
   by checking `[DomainPublisher] Upload complete — success: N` in the test's stdout (0 successes ⇒
   nothing new took effect) and confirmed by re-reading the `PATCH` scope's
   `vnext.flow.version` in the orchestration logs. Re-bumped to `1.1.1`. Fix (2) similarly bumped
   `money-transfer-master` to `1.1.0`, repinned the workflow's `attributes.schema.version`
   reference, and bumped the workflow again to `1.1.2`.

A process note on how (1) was applied: an early attempt used `json.dump(..., indent=2)` to patch just
the embedded base64 `code` field in `money-transfer.json`, which silently re-serialized the *entire*
file and escaped every non-ASCII character (Turkish labels) into `\uXXXX` — a ~30-line unrelated diff
across unrelated states. Caught via `git diff` before committing; reverted with `git checkout --` and
redone as a surgical string replacement of just the one base64 blob, verified by re-decoding every
embedded `.csx` reference in the file and comparing byte-for-byte against the source `.csx` files
(all 8 matched exactly, both before and after).

Test evolution across the gRPC run, in order:
1. Before any fix: 2/5 passed (fault #1, `filter.invalidJson`/`sort.invalidJson`).
2. After fix (1), version not bumped: 2/5 passed, identical fault — because the fix never published.
3. After bumping to 1.1.1: 2/5 passed, **new** fault (`Field 'targetIban' is not filterable'`) —
   confirms fix (1) *did* take effect (the filter JSON parsed) and surfaced the next, deeper problem.
4. After fix (2) + bump to 1.1.2: **5/5 passed.**

## Transport switches (Step 4/5)

- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`:
  `ExecutionApi.Transport` flipped `http → grpc → http`.
- `etc/docker/docker-compose.yml`, `vnext-execution-dapr` service: `--app-port` flipped
  `4202 → 4212` with `--app-protocol grpc` added, then reverted.
- Both diffs are byte-identical to the pre-change state after Step 5 (`git diff` on both files is
  empty as of this report).
- Sidecar recreated with `docker compose up -d --force-recreate vnext-execution-dapr` for both
  transitions. Sidecar log confirms the flip each time: gRPC mode waited ~36s for "application
  discovered on port 4212" (the execution app's Kestrel h2c endpoint); HTTP mode initialized in 35ms
  (no discovery wait, since the sidecar just proxies to the always-listening port 4202).
- Orchestration app was killed (`kill -9`, since a plain `kill` did not stop the backgrounded
  `dotnet run` + child process pair) and restarted between transports; execution app, and both
  workers, were left running unchanged throughout (the execution app always serves both 4202 HTTP and
  4212 gRPC regardless of which one the sidecar is configured to use).

## Test output

**gRPC run (final, after all fixes), `dotnet test ... --filter "FullyQualifiedName~MoneyTransferTests"`:**
```
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.SubmitDetails_RejectsAPayloadThatViolatesTheTransitionSchema [156 ms]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.AwaitingPushApproval_ArmsTheTimeoutTimer [728 ms]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.ExecutingTransfer_RecordsTheProvidersResultInInstanceData [1 s]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.HappyPath_ReachesTransferCompleted [1 s]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.Cancel_MovesTheTransferToCancelled [717 ms]

Total tests: 5
     Passed: 5
 Total time: 1.3779 Minutes
```

**HTTP run, same filter:**
```
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.SubmitDetails_RejectsAPayloadThatViolatesTheTransitionSchema [625 ms]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.AwaitingPushApproval_ArmsTheTimeoutTimer [1 s]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.ExecutingTransfer_RecordsTheProvidersResultInInstanceData [1 s]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.HappyPath_ReachesTransferCompleted [1 s]
Passed Core.IntegrationTests.Tests.MoneyTransfer.MoneyTransferTests.Cancel_MovesTheTransferToCancelled [639 ms]

Total tests: 5
     Passed: 5
 Total time: 1.4082 Minutes
```

## Trace evidence

Both traces cover a `TransitionJob.Execute/approve-push` transaction — the transition whose
`onEntries` runs both `execute-transfer` (HTTP task) and `get-accounts-dapr` (DaprService task) on
the `executing-transfer` state entry. Found via Elastic (`http://localhost:9200`,
`.ds-traces-apm*,traces-apm*`), queried with Python `urllib` (curl is blocked in this environment).
Reported exactly as observed — no interpretation applied beyond noting which framework name and span
name appear at each hop.

### gRPC run

- **Trace id**: `00bfd204632fe9829037aa319fc0a298`
- **Instance id**: `2db9daa4-f1ee-454e-a154-7820d21fb2fb`
- Elastic doc count for this trace: 137
- `get-accounts-dapr` span present: **yes** (`Task.Execute.get-accounts-dapr`, 82358us)
- Client-side span for the orchestration→execution hop: **`bbt.workflow.execution.v1.TaskInvoker/Invoke`**,
  `service.framework.name = OpenTelemetry.Instrumentation.GrpcNetClient` — a gRPC client span, not
  an HTTP one, both for `execute-transfer` and for `get-accounts-dapr`.
- Execution-side transaction: `POST /bbt.workflow.execution.v1.TaskInvoker/Invoke`
  (`service.name = vnext-execution-app`, `service.framework.name = Microsoft.AspNetCore`) — **same
  `trace.id`** as the orchestration side, `parent.id` chained through the intermediate
  `dapr-diagnostics` span (`/bbt.workflow.execution.v1.TaskInvoker/Invoke`). A second
  execution-side transaction with the same name and `service.framework.name = dapr-diagnostics`
  also appears in the trace for both task invocations.

Full span tree (indentation = parent/child, sorted by timestamp; `id`/`parent` are the first 8 hex
chars of `span.id`/`transaction.id` / `parent.id`):

```
[transaction] PATCH api/v{version:apiVersion}/{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey} | svc=vnext-app fw=Microsoft.AspNetCore dur=31203us id=b72d9f38 parent=None
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1018us id=0fad1c0a parent=b72d9f38
  [span] Cache.Get/sys-flows:core:money-transfer:full:1.1.2-pkg.1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=192us id=d3a8b333 parent=b72d9f38
  [span] Transition.LoadContext | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2502us id=948b2579 parent=b72d9f38
    [span] Instance.Load | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2478us id=45fc499c parent=948b2579
      [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=724us id=59d78855 parent=45fc499c
      [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=766us id=f388c126 parent=45fc499c
      [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=637us id=c3aff86d parent=45fc499c
  [span] Transition.Validate | svc=vnext-app fw=BBT.Workflow.Pipeline dur=12us id=7a8f01d9 parent=b72d9f38
    [span] Transition.ValidatePolicy | svc=vnext-app fw=BBT.Workflow.Pipeline dur=9us id=8c8105b5 parent=7a8f01d9
  [span] Lock.Acquire/vnext:core:money-transfer:2db9daa4-f1ee-454e-a154-7820d21fb2fb | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1290us id=7da15df6 parent=b72d9f38
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1298us id=e2dce8e1 parent=b72d9f38
  [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=498us id=a7a195c6 parent=b72d9f38
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=800us id=8fb9067d parent=b72d9f38
  [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2789us id=c7cab1ae parent=b72d9f38
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1308us id=439b0dc0 parent=b72d9f38
  [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=4742us id=ff90d680 parent=b72d9f38
  [span] Lock.Release/vnext:core:money-transfer:2db9daa4-f1ee-454e-a154-7820d21fb2fb | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1191us id=58bbace1 parent=b72d9f38
  [span] Events.PublishDeferred | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2us id=7313aea5 parent=b72d9f38
  [span] Uow.Commit | svc=vnext-app fw=BBT.Workflow.Pipeline dur=44us id=ef314b71 parent=b72d9f38
  [transaction] TransitionJob.Execute/approve-push | svc=vnext-app fw=BBT.Workflow.BackgroundJobs dur=876339us id=31a7dcb4 parent=b72d9f38
    [span] Cache.Get/sys-flows:core:money-transfer:full:1.1.2-pkg.1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=148us id=048923b3 parent=31a7dcb4
    [span] SyncTransitionStrategy.ExecuteAsync | svc=vnext-app fw=BBT.Aether.Aspects dur=866456us id=5a028a2d parent=31a7dcb4
      [span] Transition.LoadContext | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1494us id=70bff58e parent=5a028a2d
        [span] Instance.Load | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1474us id=9d7b61ea parent=70bff58e
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=514us id=d81a65cb parent=9d7b61ea
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=360us id=d078b394 parent=9d7b61ea
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=278us id=58aa7388 parent=9d7b61ea
      [span] Transition.ValidatePolicy | svc=vnext-app fw=BBT.Workflow.Pipeline dur=8us id=a682f295 parent=5a028a2d
      [span] Step.CreateTransitionRecord | svc=vnext-app fw=BBT.Workflow.Pipeline dur=7784us id=385b5764 parent=5a028a2d
        [span] Db.Query | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=291us id=72165814 parent=385b5764
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=205us id=fc00c9f1 parent=385b5764
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=178us id=e3d8414b parent=385b5764
        [span] Instance.AppendData | svc=vnext-app fw=BBT.Workflow.Pipeline dur=0us id=d8265ec3 parent=385b5764
        [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=3174us id=c866e1e4 parent=385b5764
      [span] Step.CancelScheduledJobs | svc=vnext-app fw=BBT.Workflow.Pipeline dur=17106us id=b7f6549e parent=5a028a2d
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=728us id=de3c65cf parent=b7f6549e
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=211us id=e1be79a7 parent=b7f6549e
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=589us id=72f4c558 parent=b7f6549e
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=3483us id=bb0b267f parent=b7f6549e
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=514us id=5a45e41c parent=b7f6549e
      [span] Step.ChangeState | svc=vnext-app fw=BBT.Workflow.Pipeline dur=3018us id=927a156b parent=5a028a2d
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2612us id=f7a9d049 parent=927a156b
      [span] Step.RunOnEntryTasks | svc=vnext-app fw=BBT.Workflow.Pipeline dur=810397us id=e168d374 parent=5a028a2d
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=908us id=2b7b8c6c parent=e168d374
        [span] Task.Execute.execute-transfer | svc=vnext-app fw=BBT.Aether.Aspects dur=725727us id=3add0713 parent=e168d374
          [span] Cache.GenerationGet/sys-tasks:core:execute-transfer:gen | svc=vnext-app fw=BBT.Workflow.Cache dur=2022us id=baddd389 parent=3add0713
          [span] Cache.Get/sys-tasks:core:execute-transfer:res:980fafe6c4a044f08f548a36ae224236:1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=7035us id=00a7116a parent=3add0713
            [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2939us id=10000539 parent=00a7116a
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=999us id=f9923e05 parent=3add0713
          [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=942us id=a4344be0 parent=3add0713
          [span] Task.PrepareInput | svc=vnext-app fw=BBT.Workflow.Tasks dur=91971us id=5697b0d1 parent=3add0713
            [span] Script.Compile | svc=vnext-app fw=BBT.Workflow.Scripting dur=58649us id=92a71a21 parent=5697b0d1
          [span] Task.Invoke | svc=vnext-app fw=BBT.Workflow.Tasks dur=603517us id=edbfc9cc parent=3add0713
            [span] bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-app fw=OpenTelemetry.Instrumentation.GrpcNetClient dur=591702us id=6f2a6b59 parent=edbfc9cc
              [span] POST | svc=vnext-app fw=System.Net.Http dur=587552us id=f38e7dfa parent=6f2a6b59
                [span] /bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-app fw=dapr-diagnostics dur=586221us id=250f6452 parent=f38e7dfa
                  [transaction] /bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-execution-app fw=dapr-diagnostics dur=578963us id=73fdf57a parent=250f6452
                  [transaction] POST /bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-execution-app fw=Microsoft.AspNetCore dur=539261us id=f2b3690a parent=250f6452
                    [span] Invoke.http/execute-transfer | svc=vnext-execution-app fw=BBT.Workflow.Execution.Invokers dur=478676us id=e2314391 parent=f2b3690a
                      [span] POST | svc=vnext-execution-app fw=System.Net.Http dur=470269us id=6fbd8a64 parent=e2314391
          [span] Task.ProcessOutput | svc=vnext-app fw=BBT.Workflow.Tasks dur=3345us id=8973fa03 parent=3add0713
          [span] Db.Query | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=921us id=bb5ed975 parent=3add0713
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=592us id=fc4d6916 parent=3add0713
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=540us id=230aa694 parent=3add0713
          [span] Instance.AppendData | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2516us id=495161cf parent=3add0713
            [span] Cache.Get/sys-schemas:core:money-transfer-master:res:07721b3aad51420ca9336a1c916655bf:1.1.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=21us id=140dcf1c parent=495161cf
            [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=569us id=eb4d69a8 parent=495161cf
            [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=506us id=20dd5d31 parent=495161cf
            [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=499us id=3e411c48 parent=495161cf
          [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=755us id=c4ab5cc2 parent=3add0713
        [span] Task.Execute.get-accounts-dapr | svc=vnext-app fw=BBT.Aether.Aspects dur=82358us id=a16c9f2b parent=e168d374
          [span] Cache.GenerationGet/sys-tasks:core:get-accounts-dapr:gen | svc=vnext-app fw=BBT.Workflow.Cache dur=1130us id=b5560db0 parent=a16c9f2b
          [span] Cache.Get/sys-tasks:core:get-accounts-dapr:res:89e3fa87c7de47e78b189ce6d60af4cf:1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=45us id=656946d0 parent=a16c9f2b
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1116us id=05d2dab1 parent=a16c9f2b
          [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=496us id=8fb92e99 parent=a16c9f2b
          [span] Task.PrepareInput | svc=vnext-app fw=BBT.Workflow.Tasks dur=21652us id=4ac9fc04 parent=a16c9f2b
            [span] Script.Compile | svc=vnext-app fw=BBT.Workflow.Scripting dur=20299us id=622dd5c6 parent=4ac9fc04
          [span] Task.Invoke | svc=vnext-app fw=BBT.Workflow.Tasks dur=47467us id=41d82d57 parent=a16c9f2b
            [span] bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-app fw=OpenTelemetry.Instrumentation.GrpcNetClient dur=46255us id=13c2a223 parent=41d82d57
              [span] POST | svc=vnext-app fw=System.Net.Http dur=46180us id=7120356c parent=13c2a223
                [span] /bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-app fw=dapr-diagnostics dur=45448us id=946c6a5e parent=7120356c
                  [transaction] POST /bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-execution-app fw=Microsoft.AspNetCore dur=42771us id=3f5b9f2a parent=946c6a5e
                    [span] Invoke.daprservice/get-accounts-dapr | svc=vnext-execution-app fw=BBT.Workflow.Execution.Invokers dur=40170us id=8491813e parent=3f5b9f2a
                      [span] Dapr invoke mocklab | svc=vnext-execution-app fw=System.Net.Http dur=36452us id=0819c336 parent=8491813e
                        [span] CallLocal/mocklab/api/payments/accounts | svc=vnext-execution-app fw=dapr-diagnostics dur=33217us id=d4391a68 parent=0819c336
                  [transaction] /bbt.workflow.execution.v1.TaskInvoker/Invoke | svc=vnext-execution-app fw=dapr-diagnostics dur=43896us id=ced8afdb parent=946c6a5e
          [span] Task.ProcessOutput | svc=vnext-app fw=BBT.Workflow.Tasks dur=3019us id=d47a6c01 parent=a16c9f2b
          [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=468us id=1db8e290 parent=a16c9f2b
      [span] Step.RunAutomaticTransitions | svc=vnext-app fw=BBT.Workflow.Pipeline dur=19585us id=d89389dc parent=5a028a2d
        [span] Script.Compile | svc=vnext-app fw=BBT.Workflow.Scripting dur=16972us id=029ab38c parent=d89389dc
      [span] Step.FinalizeTransition | svc=vnext-app fw=BBT.Workflow.Pipeline dur=3018us id=431f220f parent=5a028a2d
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=689us id=c83250c4 parent=431f220f
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2031us id=6c420ca4 parent=431f220f
      [span] Transition.Continuation/Enqueue | svc=vnext-app fw=BBT.Workflow.Pipeline dur=3901us id=bd9c4160 parent=5a028a2d
        [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=391us id=e672339f parent=bd9c4160
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=343us id=16be73b6 parent=bd9c4160
      [span] Transition.Settle | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1us id=e7f2ab72 parent=5a028a2d
    [span] Events.PublishDeferred | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1us id=024b5d51 parent=31a7dcb4
    [span] Uow.Commit | svc=vnext-app fw=BBT.Workflow.Pipeline dur=6445us id=9853fc37 parent=31a7dcb4
      [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=3741us id=cb2169ed parent=9853fc37
    [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=496us id=8b266ae3 parent=31a7dcb4
    [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1768us id=b40c71c8 parent=31a7dcb4
  [transaction] TransitionJob.Execute/execution-succeeded | svc=vnext-app fw=BBT.Workflow.BackgroundJobs dur=22744us id=71a1c24a parent=b72d9f38
    (post-executing-transfer auto-chain; DB/pipeline spans only, no further task invocations, omitted for brevity)
```

A second gRPC-run trace exists for the other test that reaches `executing-transfer`
(`ExecutingTransfer_RecordsTheProvidersResultInInstanceData`): trace id
`6a53765e3e36581369b93f877bf10918`, instance `a0e367ca-6625-47ce-a217-7ff373af179c`, same shape
(`get-accounts-dapr` completed in 22ms task-total; `bbt.workflow.execution.v1.TaskInvoker/Invoke`
gRPC client span present). Not expanded here to avoid duplicating the tree.

### HTTP run

- **Trace id**: `bfca17c6a6bf8e01e0c96160a174246e`
- **Instance id**: `e3243d2d-4c99-4580-9ccd-4b6daee226df`
- Elastic doc count for this trace: 134
- `get-accounts-dapr` span present: **yes** (`Task.Execute.get-accounts-dapr`, 174331us)
- Client-side span for the orchestration→execution hop: **`Dapr invoke vnext-execution-app`**,
  `service.framework.name = System.Net.Http` (plain HTTP client, not gRPC), followed by a
  `dapr-diagnostics` span named `CallLocal/vnext-execution-app/api/v1/execution/invoke/daprservice/get-accounts-dapr`
  — a different span name/shape from the gRPC run's `.../TaskInvoker/Invoke`, reflecting the
  different Dapr service-invocation code path (HTTP proxy vs gRPC proxy).
- Execution-side transaction: `POST api/v{version:apiVersion}/execution/invoke/{type}/{key}`
  (`service.name = vnext-execution-app`, `service.framework.name = Microsoft.AspNetCore`) — **same
  `trace.id`** as the orchestration side, `parent.id` chained through the intermediate
  `dapr-diagnostics` transaction `CallLocal/vnext-execution-app/api/v1/execution/invoke/daprservice/get-accounts-dapr`.

Full span tree:

```
[transaction] PATCH api/v{version:apiVersion}/{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey} | svc=vnext-app fw=Microsoft.AspNetCore dur=25209us id=f6b4dcba parent=None
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1184us id=f965ff0f parent=f6b4dcba
  [span] Cache.Get/sys-flows:core:money-transfer:full:1.1.2-pkg.1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=177us id=b1956cfd parent=f6b4dcba
  [span] Transition.LoadContext | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2502us id=17ce371f parent=f6b4dcba
    [span] Instance.Load | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2472us id=4b4b5d20 parent=17ce371f
      [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=653us id=aad3b065 parent=4b4b5d20
      [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=840us id=f9f2e404 parent=4b4b5d20
      [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=636us id=bbe576f3 parent=4b4b5d20
  [span] Transition.Validate | svc=vnext-app fw=BBT.Workflow.Pipeline dur=22us id=5e7d2550 parent=f6b4dcba
    [span] Transition.ValidatePolicy | svc=vnext-app fw=BBT.Workflow.Pipeline dur=19us id=ccdfbc22 parent=5e7d2550
  [span] Lock.Acquire/vnext:core:money-transfer:e3243d2d-4c99-4580-9ccd-4b6daee226df | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1626us id=cf1c82fd parent=f6b4dcba
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1347us id=d14e81bf parent=f6b4dcba
  [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=543us id=91941ff2 parent=f6b4dcba
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=749us id=45cbb0ea parent=f6b4dcba
  [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2093us id=cb9576f3 parent=f6b4dcba
  [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1879us id=e128e6f5 parent=f6b4dcba
  [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2639us id=28b56e31 parent=f6b4dcba
  [span] Lock.Release/vnext:core:money-transfer:e3243d2d-4c99-4580-9ccd-4b6daee226df | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1192us id=688d5a23 parent=f6b4dcba
  [span] Events.PublishDeferred | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1us id=baff7b05 parent=f6b4dcba
  [span] Uow.Commit | svc=vnext-app fw=BBT.Workflow.Pipeline dur=44us id=51ccb053 parent=f6b4dcba
  [transaction] TransitionJob.Execute/approve-push | svc=vnext-app fw=BBT.Workflow.BackgroundJobs dur=834440us id=dc2e4304 parent=f6b4dcba
    [span] Cache.Get/sys-flows:core:money-transfer:full:1.1.2-pkg.1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=158us id=43685bcf parent=dc2e4304
    [span] SyncTransitionStrategy.ExecuteAsync | svc=vnext-app fw=BBT.Aether.Aspects dur=827173us id=b9016f7c parent=dc2e4304
      [span] Transition.LoadContext | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1832us id=1f7f83be parent=b9016f7c
        [span] Instance.Load | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1801us id=81467ba7 parent=1f7f83be
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=517us id=d5a49ee1 parent=81467ba7
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=556us id=f8757708 parent=81467ba7
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=432us id=da00c80f parent=81467ba7
      [span] Transition.ValidatePolicy | svc=vnext-app fw=BBT.Workflow.Pipeline dur=11us id=b407b085 parent=b9016f7c
      [span] Step.CreateTransitionRecord | svc=vnext-app fw=BBT.Workflow.Pipeline dur=4321us id=ec9810af parent=b9016f7c
        [span] Db.Query | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=257us id=e15a3c7f parent=ec9810af
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=199us id=65a8f333 parent=ec9810af
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=213us id=f3597854 parent=ec9810af
        [span] Instance.AppendData | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1us id=b39cf72a parent=ec9810af
        [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1285us id=fd3176c6 parent=ec9810af
      [span] Step.CancelScheduledJobs | svc=vnext-app fw=BBT.Workflow.Pipeline dur=31384us id=25071432 parent=b9016f7c
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1812us id=bc8ee010 parent=25071432
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=897us id=d3b2cd7e parent=25071432
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1025us id=ec473f6b parent=25071432
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=897us id=9da4cb7d parent=25071432
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=416us id=351baf35 parent=25071432
      [span] Step.ChangeState | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2760us id=0d7f8851 parent=b9016f7c
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2302us id=ddf1a5e6 parent=0d7f8851
      [span] Step.RunOnEntryTasks | svc=vnext-app fw=BBT.Workflow.Pipeline dur=754575us id=e8b925fb parent=b9016f7c
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1101us id=270fa766 parent=e8b925fb
        [span] Task.Execute.execute-transfer | svc=vnext-app fw=BBT.Aether.Aspects dur=577704us id=3f9caac6 parent=e8b925fb
          [span] Cache.GenerationGet/sys-tasks:core:execute-transfer:gen | svc=vnext-app fw=BBT.Workflow.Cache dur=1273us id=2aaf30db parent=3f9caac6
          [span] Cache.Get/sys-tasks:core:execute-transfer:res:980fafe6c4a044f08f548a36ae224236:1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=1738us id=de0981ec parent=3f9caac6
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1382us id=3f16d517 parent=3f9caac6
          [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=796us id=98a58334 parent=3f9caac6
          [span] Task.PrepareInput | svc=vnext-app fw=BBT.Workflow.Tasks dur=71983us id=bf0c95cb parent=3f9caac6
            [span] Script.Compile | svc=vnext-app fw=BBT.Workflow.Scripting dur=59717us id=536e3eea parent=bf0c95cb
          [span] Task.Invoke | svc=vnext-app fw=BBT.Workflow.Tasks dur=483565us id=bfbd2b06 parent=3f9caac6
            [span] Dapr invoke vnext-execution-app | svc=vnext-app fw=System.Net.Http dur=475391us id=58070f5b parent=bfbd2b06
              [span] CallLocal/vnext-execution-app/api/v1/execution/invoke/http/execute-transfer | svc=vnext-app fw=dapr-diagnostics dur=469176us id=0bc4b102 parent=58070f5b
                [transaction] CallLocal/vnext-execution-app/api/v1/execution/invoke/http/execute-transfer | svc=vnext-execution-app fw=dapr-diagnostics dur=462011us id=392959d5 parent=0bc4b102
                  [transaction] POST api/v{version:apiVersion}/execution/invoke/{type}/{key} | svc=vnext-execution-app fw=Microsoft.AspNetCore dur=442876us id=e9f13e30 parent=392959d5
                    [span] Invoke.http/execute-transfer | svc=vnext-execution-app fw=BBT.Workflow.Execution.Invokers dur=390181us id=bb5cc220 parent=e9f13e30
                      [span] POST | svc=vnext-execution-app fw=System.Net.Http dur=387035us id=4348b8d5 parent=bb5cc220
          [span] Task.ProcessOutput | svc=vnext-app fw=BBT.Workflow.Tasks dur=3687us id=71f7100d parent=3f9caac6
          [span] Db.Query | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=503us id=159b17b5 parent=3f9caac6
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=288us id=9b4e7594 parent=3f9caac6
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=244us id=5b575b24 parent=3f9caac6
          [span] Instance.AppendData | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2086us id=a3a1c502 parent=3f9caac6
            [span] Cache.Get/sys-schemas:core:money-transfer-master:res:07721b3aad51420ca9336a1c916655bf:1.1.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=31us id=46451354 parent=a3a1c502
            [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=396us id=48336866 parent=a3a1c502
            [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=317us id=6a1e80f2 parent=a3a1c502
            [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=344us id=5798ac5e parent=a3a1c502
          [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=370us id=5adac343 parent=3f9caac6
        [span] Task.Execute.get-accounts-dapr | svc=vnext-app fw=BBT.Aether.Aspects dur=174331us id=a26f7770 parent=e8b925fb
          [span] Cache.GenerationGet/sys-tasks:core:get-accounts-dapr:gen | svc=vnext-app fw=BBT.Workflow.Cache dur=871us id=9abc41a1 parent=a26f7770
          [span] Cache.Get/sys-tasks:core:get-accounts-dapr:res:89e3fa87c7de47e78b189ce6d60af4cf:1.0.0 | svc=vnext-app fw=BBT.Workflow.Cache dur=2269us id=43ad2f7e parent=a26f7770
          [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=589us id=03aa2ddd parent=a26f7770
          [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=354us id=23548e5f parent=a26f7770
          [span] Task.PrepareInput | svc=vnext-app fw=BBT.Workflow.Tasks dur=110109us id=da88a7d5 parent=a26f7770
            [span] Script.Compile | svc=vnext-app fw=BBT.Workflow.Scripting dur=109123us id=3efa283a parent=da88a7d5
          [span] Task.Invoke | svc=vnext-app fw=BBT.Workflow.Tasks dur=49915us id=7ab30be5 parent=a26f7770
            [span] Dapr invoke vnext-execution-app | svc=vnext-app fw=System.Net.Http dur=48494us id=80db46c3 parent=7ab30be5
              [span] CallLocal/vnext-execution-app/api/v1/execution/invoke/daprservice/get-accounts-dapr | svc=vnext-app fw=dapr-diagnostics dur=47404us id=16bbe350 parent=80db46c3
                [transaction] CallLocal/vnext-execution-app/api/v1/execution/invoke/daprservice/get-accounts-dapr | svc=vnext-execution-app fw=dapr-diagnostics dur=46320us id=25894a55 parent=16bbe350
                  [transaction] POST api/v{version:apiVersion}/execution/invoke/{type}/{key} | svc=vnext-execution-app fw=Microsoft.AspNetCore dur=44867us id=dd4463b2 parent=25894a55
                    [span] Invoke.daprservice/get-accounts-dapr | svc=vnext-execution-app fw=BBT.Workflow.Execution.Invokers dur=42909us id=7f444679 parent=dd4463b2
                      [span] Dapr invoke mocklab | svc=vnext-execution-app fw=System.Net.Http dur=42666us id=65012c4b parent=7f444679
                        [span] CallLocal/mocklab/api/payments/accounts | svc=vnext-execution-app fw=dapr-diagnostics dur=40030us id=fd1fb315 parent=65012c4b
          [span] Task.ProcessOutput | svc=vnext-app fw=BBT.Workflow.Tasks dur=3183us id=62a0ec14 parent=a26f7770
          [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=466us id=850c075b parent=a26f7770
      [span] Step.RunAutomaticTransitions | svc=vnext-app fw=BBT.Workflow.Pipeline dur=26227us id=e4d4934d parent=b9016f7c
        [span] Script.Compile | svc=vnext-app fw=BBT.Workflow.Scripting dur=23565us id=d452af20 parent=e4d4934d
      [span] Step.FinalizeTransition | svc=vnext-app fw=BBT.Workflow.Pipeline dur=2581us id=865e9e28 parent=b9016f7c
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=691us id=b0fd2155 parent=865e9e28
        [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1562us id=3c0962ea parent=865e9e28
      [span] Transition.Continuation/Enqueue | svc=vnext-app fw=BBT.Workflow.Pipeline dur=3295us id=a423d2fa parent=b9016f7c
        [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=485us id=4ca09066 parent=a423d2fa
        [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=351us id=9285a411 parent=a423d2fa
      [span] Transition.Settle | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1us id=975ab5d9 parent=b9016f7c
    [span] Events.PublishDeferred | svc=vnext-app fw=BBT.Workflow.Pipeline dur=1us id=4653b6c8 parent=dc2e4304
    [span] Uow.Commit | svc=vnext-app fw=BBT.Workflow.Pipeline dur=4617us id=16e17bb2 parent=dc2e4304
      [span] Db.INSERT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=2092us id=ecbabf9a parent=16e17bb2
    [span] Db.SELECT | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=386us id=f5d722bb parent=dc2e4304
    [span] Db.UPDATE | svc=vnext-app fw=OpenTelemetry.Instrumentation.EntityFrameworkCore dur=1365us id=ccea270f parent=dc2e4304
  [transaction] TransitionJob.Execute/execution-succeeded | svc=vnext-app fw=BBT.Workflow.BackgroundJobs dur=43068us id=3c731a1f parent=f6b4dcba
    (post-executing-transfer auto-chain; DB/pipeline spans only, omitted for brevity)
```

## Side-by-side comparison, key spans only

| | gRPC run | HTTP run |
|---|---|---|
| Trace id | `00bfd204632fe9829037aa319fc0a298` | `bfca17c6a6bf8e01e0c96160a174246e` |
| Instance id | `2db9daa4-f1ee-454e-a154-7820d21fb2fb` | `e3243d2d-4c99-4580-9ccd-4b6daee226df` |
| Elastic doc count | 137 | 134 |
| `Task.Execute.get-accounts-dapr` duration | 82358us | 174331us |
| Client hop span name | `bbt.workflow.execution.v1.TaskInvoker/Invoke` | `Dapr invoke vnext-execution-app` |
| Client hop framework | `OpenTelemetry.Instrumentation.GrpcNetClient` | `System.Net.Http` |
| Intermediate dapr-diagnostics span name | `/bbt.workflow.execution.v1.TaskInvoker/Invoke` | `CallLocal/vnext-execution-app/api/v1/execution/invoke/daprservice/get-accounts-dapr` |
| Execution-side transaction name | `POST /bbt.workflow.execution.v1.TaskInvoker/Invoke` | `POST api/v{version:apiVersion}/execution/invoke/{type}/{key}` |
| Execution-side `service.name` | `vnext-execution-app` | `vnext-execution-app` |
| Execution-side same `trace.id` as orchestration | yes | yes |
| `Invoke.daprservice/get-accounts-dapr` span present on execution side | yes | yes |
| `Dapr invoke mocklab` → `CallLocal/mocklab/api/payments/accounts` present | yes | yes |

No conclusions drawn beyond what is listed above — the observation is limited to span names, framework
identifiers, durations, and trace/parent id continuity as requested.

## Cleanup

- vnext repo: `git diff` on `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`
  and `etc/docker/docker-compose.yml` is empty (both reverted to `Transport: "http"` / no
  `--app-protocol grpc`).
- All 4 apps stopped (`pkill -f "dotnet run"` then `pkill -f "BBT.Workflow"`).
- 19 containers left running, untouched.
- vnext-example: 4 commits left in place on `feature/caller-role-provider` (local only, not pushed).
