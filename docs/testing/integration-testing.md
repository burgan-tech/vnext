# Integration Testing a Runtime Change

How a change to this runtime is verified end to end, using the example domain and the testing SDK
that live in sibling repositories. This page is the **contract**; the runnable procedure is the
`runtime-integration-test` skill (`.claude/skills/runtime-integration-test/SKILL.md`). Every other
agent (Codex, Cursor, Copilot, Gemini) reads this page directly.

Related: [Platform repositories](../../AGENTS.md#platform-repositories) (who owns what),
[Runbook: bring up domain X](../../AGENTS.md#runbook-bring-up-domain-x-for-agents),
[Correlation and tracing](../monitoring/correlation-and-tracing.md),
[Elastic queries for the trace tree](../runtime/trace-elastic-queries.md).

## 1. Policy — when an integration test is required

Unit tests are **not** sufficient evidence for a change to a core process. Whether a change to the
pipeline, transitions, subflows, locking, instance data, the error boundary or the state function
actually behaves as intended is only shown by an integration test against a running runtime.

| Situation | Decision |
|---|---|
| Major change; pipeline / process behaviour changes | **Write and run** |
| Risk of regressing a working feature | **Write and run** |
| Small fix, isolated method, log/doc/refactor | **Do not** — unit tests are enough |
| Unsure | **Propose it to the user with your reasoning and wait** — do not start on your own |

Not every change gets an integration test: the cost is real (infrastructure plus four hosts). Reach
for it when the change is serious; when it is borderline, recommend and wait for approval.

**Never verify a runtime change against a container image.** The image carries released code, not
your working tree. The test SDK's default (Testcontainers) starts that image, which is why
[§4](#4-wire-the-tests-to-the-local-runtime) points it at the locally built runtime instead.

Tests and example flows are written in **vnext-example**, never in this repository. This repo's
`test/` holds unit tests only.

## 2. Where things live and how to find them

| Need | Repository | Path inside it |
|---|---|---|
| Example flows (19 workflows under domain `core`, plus `partner`) | vnext-example | `core/Workflows/<flow>/`, `partner/` |
| Integration test project | vnext-example | `tests/Core.IntegrationTests/` |
| Scenario index (**must** be updated) | vnext-example | `TEST-SCENARIOS.md` |
| Hand-driven `.http` files, Python behaviour/load tests | vnext-example | `api-tests/<scenario>/` |
| MockLab compose + seeds | vnext-example | `docker-compose.yml`, `etc/docker/config/seed/` |
| Cross-domain lab (core + partner + discovery) | vnext-example | `labs/cross-domain/` |
| Testing SDK (`VNext.Testing.Sdk`, `VNext.Testing.Template`) | vnext-integration-test | `src/`, `GETTING_STARTED.md`, `README.md` |

**Sibling layout.** Every platform repo is expected as a sibling checkout: `../vnext-example`,
`../vnext-integration-test` (an older clone may be named `vnext-integration`), `../aether`, and so
on. This is the same convention `nuget.config` (`../aether/.local-feed`), `labs/cross-domain/lab.sh`
(`../vnext`) and the runbook in `AGENTS.md` already rely on. Resolution rule for an agent:

1. Look for `../<repo>` relative to this checkout.
2. If it is missing, **ask once**: clone it there (`git clone https://github.com/burgan-tech/<repo>.git ../<repo>`)
   or use an existing checkout the user names.
3. Remember the answer — Claude Code in its auto-memory, other agents in the developer's
   machine-local notes (`CLAUDE.local.md`, git-ignored). Do not ask again in later sessions.
4. Never write an absolute path (`/Users/...`, `C:\...`) into a committed file.

**The SDK is ours.** When you need to know what `VNext.Testing.Sdk` does — fixture lifecycle, the
`VNextApiClient` surface, how `VNEXT_BASE_URL` is resolved — read the SDK source, do not guess. If the
SDK lacks a capability the scenario needs, **report it to the user**; do not quietly write a
workaround into the test project. (Known limits: `StartInstanceAsync`/`RunTransitionAsync` hard-code
`?sync=true`; there is no built-in wait/poll helper — vnext-example's `WorkflowTestBase` adds both.)

## 3. Bring up the local runtime

Check first, never restart a running stack:

```bash
cd etc/docker && ./run-docker.sh status
ls ai-docs/local-environments/            # records of domains brought up on this machine
```

**Primary path (no terminal needed).** `./run-docker.sh up <domain> [--offset N]` starts the
infrastructure, sidecars, DbMigrator and the four hosts as local binaries, waits for `/health`, and
writes `ai-docs/local-environments/<domain>.md` — which contains the base URL, database, app-ids and
the exact `VNEXT_BASE_URL=...` line for the tests. Read that record instead of recomputing ports. The
full procedure, offsets and `wf` registration are in the
[runbook](../../AGENTS.md#runbook-bring-up-domain-x-for-agents).

**Manual path (one terminal per host).** Infrastructure first (`./run-docker.sh`, default = infra
only). Then, if the change carries a migration, DbMigrator **once** — its Dapr sidecar shuts itself
down after every run, so bring the sidecar up before each run:

```bash
cd etc/docker && docker compose up -d vnext-db-migrator-dapr && cd ../..
dotnet run --project workers/BBT.Workflow.DbMigrator --launch-profile DbMigrator
```

Then the four hosts, **always with `--launch-profile http`**:

```bash
dotnet run --project orchestration/BBT.Workflow.Orchestration.HttpApi.Host --launch-profile http  # 4201
dotnet run --project execution/BBT.Workflow.Execution.HttpApi.Host --launch-profile http          # 4202
dotnet run --project workers/BBT.Workflow.Workers.Inbox --launch-profile http                    # 4501
dotnet run --project workers/BBT.Workflow.Workers.Outbox --launch-profile http                   # 4401
```

Without the launch profile the process still starts, but `APP_DOMAIN`, `DAPR_*` and `OTEL_*` from
`Properties/launchSettings.json` are missing, and Dapr pub/sub and lock connections fail late and
misleadingly.

**MockLab** (HTTP mocks used by most example flows) runs from vnext-example, on the `bbt-development`
Docker network that this repo's infrastructure creates — so infra first, MockLab second:

```bash
cd ../vnext-example && docker compose up -d      # MockLab on localhost:3001
```

Seeds under `etc/docker/config/seed/` are imported **only for collection names that do not exist
yet**; after editing a seed, `docker compose down -v && docker compose up -d`. MockLab routes match by
**prefix** (a mock at `documents/process` also answers `documents/process-slow`).

## 4. Wire the tests to the local runtime

The SDK reads `VNEXT_BASE_URL`:

- **Set** → external mode. No containers are created; the SDK publishes the domain's components
  (`core/**`) to that URL through `LocalDomainPublisher` and returns. `OnAfterEnvironmentReadyAsync`
  is **never called** in this mode — anything a scenario needs beyond the `core` publish (for example
  the `partner` domain) must be done in the scenario's own fixture, as `CrossDomainLabFixture` does.
- **Unset** → Testcontainers mode: the SDK starts PostgreSQL, Redis, Vault, Dapr, the runtime
  **image**, MockLab and the migrator. Correct for domain teams, **wrong for verifying a runtime change**.

`tests/Core.IntegrationTests/test.runsettings` is committed with the external mode already on:

```xml
<VNEXT_BASE_URL>http://localhost:4201</VNEXT_BASE_URL>
<VNEXT_PARTNER_BASE_URL>http://localhost:4211</VNEXT_PARTNER_BASE_URL>   <!-- unset ⇒ CrossDomainLab skips -->
<MOCKLAB_BASE_URL>http://localhost:3001</MOCKLAB_BASE_URL>               <!-- unreachable ⇒ 2 retry tests skip -->
```

Verify it is set (do not assume it is commented out). For a different port or offset, do **not** edit
the committed file: create the git-ignored `test.runsettings.local` next to it — the csproj prefers it,
and so does the SDK, which parses these files itself. Precedence: real environment variable >
`test.runsettings.local` > `test.runsettings`. `VNEXT_IT_SKIP_PUBLISH` is **not** implemented; do not
document or rely on it.

Run one scenario at a time:

```bash
cd ../vnext-example
dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
  --filter "FullyQualifiedName~ScheduleAfterAuto" -v minimal
```

**Publishing is version-immutable.** Re-publishing the same component version with different content
answers 409 `Instance:100002`. Every fixture or flow change needs a patch bump (and updated
fully-versioned references). Alternatives to the SDK publisher: `wf domain use <domain> && wf sync`
(the CLI has no `publish` or `validate` command — `sync` adds, `update` changes, `reset` forces) and the
Python scripts' `--publish` flag.

## 5. Write or extend a scenario

Prefer **extending an existing flow** when the same case already exists — `TEST-SCENARIOS.md` lists
which scenario covers which feature set. Otherwise add a scenario, in one commit:

1. Flow under `core/Workflows/<scenario>/` (schema/task/view components in their folders). The
   `vnext-ai-toolkit` skills (`workflow-scaffold`, `component-task`, `schema-design`, `view-design`,
   `integration-test`) help, but **the plugin may lag a runtime change** — its schemas and examples may
   not know the new behaviour yet. Truth order: runtime code > vnext-docs > plugin template. Do not
   accept plugin output blindly; fix it by hand when it disagrees with the runtime.
2. Test class under `tests/Core.IntegrationTests/Tests/<Scenario>/`, deriving from
   `WorkflowTestBase`, with an XML `<summary>` stating what is checked and why.
3. A `README.md` in the scenario's test folder answering, at minimum: **what it checks** (one sentence,
   the runtime guarantee), **why it exists** (bug, issue, regression or design decision — issue/PR
   number and date; this is the most valuable part, never leave it empty), the state/transition sketch
   and which step is critical, **how to run** (commands, prerequisites: infra, MockLab, migration,
   `.http` file), and the **pass criterion** plus known limits.
4. If the scenario is measured under load or concurrency, a Python script under `api-tests/<scenario>/`
   (existing examples: `chain-busy/chain-busy-behaviour-test.py`, `script-race-lab/race-load.py`,
   `fan-out-documents/fanout-load.py`). The README must give dependencies, the run command **with its
   parameters** (base URL, concurrency, iterations, duration), what is measured, the **failure
   threshold**, and how to read the result. Prefer `--base-url` over a hard-coded `localhost:4201`.
5. **A row in `TEST-SCENARIOS.md` in the same commit.** Columns: scenario, vNext feature set tested
   (real concepts: pipeline step, profile, subflow lifecycle, error boundary, locking, state
   function/long-polling, instance data — not generic words), why added (issue/date), integration test
   path, Python test path, status. Never delete a row: mark it deprecated and say why, so history stays
   visible.

## 6. Debug a red test

Look at the runtime before touching the test. The hosts started from a terminal write no file logs;
everything is in the observability stack from `etc/docker`:

| Tool | Where | Use |
|---|---|---|
| Kibana | `http://localhost:5601` → Observability → APM → Traces | Trace tree per instance/request — the renderer to trust |
| Elasticsearch | `http://localhost:9200`, logs in `logs-apm.app.vnext_app-default*`, traces in `traces-apm*` | Search by instance id — script errors in seconds; queries in [trace-elastic-queries](../runtime/trace-elastic-queries.md) |
| OpenObserve | `http://localhost:5080` | Second opinion on the same spans and logs |
| MockLab admin | `GET http://localhost:3001/_admin/logs` | The exact body the runtime sent; a broken template shows up in the `X-Mocklab-Template-Error` response header |
| Host logs | `./run-docker.sh logs <domain> [host]` (when started with `up`) | stdout of a host |

Test-side helpers worth knowing (`WorkflowTestBase`): `RunAcceptedAsync` fails immediately with the
runtime's error body when a transition is rejected; `AssertNotFaultedAsync` after any start state that
has `onEntries`; `SendRawAsync` for header-less requests; `Headers(roles)` for the standard caller
header set. A parked auto-chain rests in **Busy** — wait for the state, not the status.

## 7. Cross-domain scenarios

A parent in `core` driving components in `partner` over Dapr needs the three-domain lab
(`labs/cross-domain/lab.sh up`, discovery on `:4231`, partner on `:4211`). Use the
`cross-domain-lab` skill; the lab README lists its own pitfalls (`vNextApi__BaseUrl` must not be
`localhost`, `DbMigrator exit 139` means a stale image, change loop is `images → down → up`).

## 8. Aether changes during development

**Do not edit Aether on your own.** When the analysis shows the framework needs a change, present it
to the user with the trade-off (an Aether change versus a permanent workaround in vnext). If the user
decides to do it, clone `../aether` (or use the path the user names) and work there.

To build and test vnext against the unreleased framework **before it is released**, use the
repo-local feed that already exists:

1. In aether: `./build/pack-local.sh [1.0.NN-local]` packs every `BBT.Aether.*` library into
   `aether/.local-feed`. Versions **must** end in `-local` (the script refuses otherwise) so they can
   never shadow a published release. The script purges `~/.nuget/packages/bbt.aether.*/<version>`
   first — without that purge, re-packing the same version silently keeps compiling against the old
   contents ("the member I just added does not exist").
2. In vnext `nuget.config`: uncomment **both** the `aether-local` package source (`../aether/.local-feed`)
   **and** its `packageSourceMapping` block. Because the file uses `<clear/>` and maps `*` to nuget.org,
   the source alone is inert.
3. In vnext `Directory.Build.props`: set `<AetherPackageVersion>` to the packed version
   (e.g. `1.0.40-local`). `dotnet restore`, build, run the integration tests.
4. **Before opening a PR, revert**: set `AetherPackageVersion` back to the released version and
   re-comment the two blocks. CI cannot restore a `-local` version. The `create-github-pr` skill checks
   for this and stops if it finds a live local feed. A branch has been pushed with `1.0.40-local`
   before; the guard exists because of it.

Recommendations for the Aether repo (not done from here, propose to the user): commit `.local-feed/`
to its `.gitignore` (today it is only in `.git/info/exclude`, per clone) and mention `pack-local.sh`
in its `CLAUDE.md`.

## 9. Environment parity (Helm)

Product teams deploy this runtime with `vnext-helm-charts` (`charts/vnext`). When a runtime change
adds a **mandatory** configuration key or environment variable, check the chart: runtime options
(`WorkflowExecution__*`, `Workflow__Scripting__*`, ...) have **no chart-side defaults** — they reach the
pods only through `appEnvConfig` / `extraEnvConfig` passthrough, and `WorkflowExecutionOptionsValidator`
fails startup on bad values. Remind the user if the chart needs a matching default or documentation.

Resources: the chart ships measured `resourcesFallback` values per component (orchestrator
200m/512Mi → 2 CPU/2Gi, execution 100m/256Mi → 2/2Gi, workers 75m/256Mi → 1/1Gi); precedence is
`<component>.resources` > `global.resources.default` > `resourcesFallback`. Domain owners change these
per environment — our job is to give an evidence-based optimum, not to hard-code it. Dapr sidecar
sizing is deliberately an environment responsibility (`charts/vnext/docs/RESOURCE_TUNING.md`); an
unsized (BestEffort) sidecar was the prime suspect in a production sidecar→app latency finding.
When a behaviour works locally but not in an environment, start the investigation in the chart
(config, env, secret, replica, probe, resource limit).

## 10. Report the result

When the integration run is part of a development, its outcome belongs in the PR body under
`## Integration test evidence`: scenario(s) run, runtime commit, base URL/offset, passed/failed counts,
and every remaining red with its cause (an environment defect, a known gap, or a real regression).
The `create-github-pr` skill carries this section in its template.

## 11. Known gaps and pitfalls

- Most Python scripts under `api-tests/` hard-code `http://localhost:4201`; only `fanout-load.py`,
  `perf-load.py` and `terminal-relay-load.py` accept `--base-url`.
- `npm run validate` in vnext-example rejects FanOut (TaskType 21) components and `useDapr` on task
  types 11–14 while the pinned `@burgan-tech/vnext-schema` lags; the SDK publisher posts raw JSON to
  `/api/v1/definitions/publish`, so tests still run. Do not use `validate` as a hard gate.
- `account-opening` is red in Testcontainers mode by design: HTTP tasks hard-code `localhost:3001`,
  which is right on the host and wrong inside a container. Local runtime + host MockLab is the only
  fully working combination for MockLab-dependent flows.
- `vnext-docs` (the published site) may lag a runtime change; it answers product and client-consumption
  questions, not "what does the code do today".
- `vnext-runtime` (compose templates, `make dev`) is an optional way to run a runtime locally; for
  runtime development, this repo's `etc/docker/run-docker.sh` plus vnext-example is normally enough.
- Publish requires the domain's system flows (`@burgan-tech/vnext-core-runtime`) to be loaded through
  that domain's init container first — see the runbook.
