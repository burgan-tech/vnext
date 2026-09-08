---
name: runtime-integration-test
description: Use when a change to this runtime touches the pipeline, transitions, subflows, locking, instance data, the error boundary or the state function and must be verified end to end against a running runtime, or when the user says "integration test", "entegrasyon testi", "integration test yaz", "integration test koş", "vnext-example'da test et", "e2e doğrula", "uçtan uca doğrula". Not for domain teams writing tests for their own workflows — that is vnext-ai-toolkit:integration-test.
---

# Runtime Integration Test

This skill is the **procedure**; the **contract** is
[docs/testing/integration-testing.md](../../../docs/testing/integration-testing.md) — read it before the
first step. Tests and flows go to **vnext-example**, never into this repo. The runtime under test is the
one built from this working tree, never a container image.

## Decide first

Apply the policy matrix (contract §1). Major process change or regression risk → run. Small fix,
refactor, docs → unit tests are enough, say so. Borderline → propose with the reason and **wait**; do
not bring up infrastructure on your own.

## Procedure

1. **Resolve sibling repos.** `ls ../vnext-example ../vnext-integration-test` (an older SDK clone may
   be named `../vnext-integration` — either is fine). Missing → ask once (clone to `../<repo>` from `https://github.com/burgan-tech/<repo>.git`, or a path
   the user names) and remember it (auto-memory). Never put an absolute path in a committed file.
2. **Check what is running.** `cd etc/docker && ./run-docker.sh status`; read
   `ai-docs/local-environments/*.md`. Never restart a running stack. If another compose file owns the
   infra (cross-domain lab), stop and report.
3. **Bring the runtime up.** `./run-docker.sh up <domain> [--offset N]` (runbook in `AGENTS.md`), then
   read `ai-docs/local-environments/<domain>.md` for the base URL — do not recompute ports. On a fresh
   database, load the **system flows** (`@burgan-tech/vnext-core-runtime`) once through the domain's init
   container (runbook step 5) — the test SDK publishes only the domain's own components, never these.
   Manual four-host path only when the user runs the hosts in their own terminals
   (`--launch-profile http`, DbMigrator with its sidecar up).
4. **MockLab** if the flows call HTTP tasks: `cd ../vnext-example && docker compose up -d` (after the
   infra — it needs the `bbt-development` network). Seed changes need `down -v`.
5. **Pick or write the scenario.** Extend an existing flow when `TEST-SCENARIOS.md` already covers the
   case. New scenario → flow + test class + scenario README + optional Python load test + index row, one
   commit (contract §5). Toolkit skills may lag the runtime: runtime code wins over the plugin template.
   Fixture/flow changes need a **patch bump** — publish is version-immutable (409 `Instance:100002`).
6. **Wire the base URL.** `VNEXT_BASE_URL` is already committed in `test.runsettings`; verify it and
   that it matches the record from step 3. Different port → git-ignored `test.runsettings.local`, never
   the committed file. Unset means Testcontainers + image = the wrong runtime.
7. **Run one scenario**:
   ```bash
   cd ../vnext-example
   dotnet test tests/Core.IntegrationTests --settings tests/Core.IntegrationTests/test.runsettings \
     --filter "FullyQualifiedName~<Scenario>" -v minimal
   ```
   The SDK publishes the domain's `core/**` components itself in this mode (system flows excluded, see
   step 3); do not also `wf sync` the same components unless the test project does not cover them.
8. **Red → runtime evidence before test edits.** Elasticsearch `logs-apm.app.vnext_app-default*`
   by instance id, Kibana APM traces, OpenObserve `:5080`, MockLab `GET /_admin/logs` +
   `X-Mocklab-Template-Error`, `./run-docker.sh logs <domain> [host]`. Decide: environment defect, known
   gap (`TEST-SCENARIOS.md` § Bilinen Kapsam Açıkları), or a real regression — never "flaky" by default.
9. **Record.** Scenario README + `TEST-SCENARIOS.md` row (or status update) in the same commit as the
   scenario. Deprecated rows are marked, never deleted.
10. **Report.** Hand the counts, runtime commit, base URL and every remaining red with its cause to the
    PR body (`## Integration test evidence`, via `create-github-pr`). If the SDK lacked something, name
    the gap to the user instead of leaving a workaround in the tests.

## Runtime facts for assertions

| Fact | Consequence in a test |
|---|---|
| SDK client hard-codes `?sync=true` | Async behaviour needs a raw `HttpClient` (`WorkflowTestBase`) |
| External mode never calls `OnAfterEnvironmentReadyAsync` | Extra publishes belong in the scenario fixture |
| Parked auto-chain rests in **Busy** | Wait for the **state**, not the status |
| `updateData` on a parent with an active SubFlow is data-only | onExecute counters do not move |
| `Headers(roles)` sends `x-roles` + `role`, `user_reference`, `x-device-id` | Role-gated state functions 403 without it |
| Aether change needed | Propose first; local feed procedure in contract §8; revert before PR |

## Red flags — stop

| Thought | Reality |
|---|---|
| "The release image is close enough" | It runs the old code; the result says nothing about the change. |
| "Skip the `TEST-SCENARIOS.md` row for now" | The row is the only history; add it in the same commit. |
| "I'll work around the SDK gap in the test" | Report it; the SDK is ours to fix. |
| "Just patch Aether directly" | Propose, wait for the decision, then use the local feed. |
| "Restart the stack to be safe" | Check `status` first; someone else's lab may own it. |
| "Hard-code my repo path, it's faster" | `../<repo>` or ask once; absolute paths never get committed. |
