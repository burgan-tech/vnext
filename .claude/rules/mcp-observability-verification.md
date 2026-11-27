# Verifying a Change Through the MCP Servers (Always Apply)

Applies whenever a change is exercised against a locally running runtime — a manual request, an
integration-test run, a load script. Governs what counts as evidence that the change works.

- **A green test run is not, by itself, a verified result.** After exercising a change, confirm it
  through the MCP servers (`.mcp.json`) and quote the measured numbers: `openobserve` for logs and the
  span tree (avg/p95/max of the touched endpoints and steps, root cause by `instanceId` / `flow` /
  `transitionKey` / trace id), `postgres` for persisted rows, `redis` for cache and lock state,
  `elasticsearch` for APM traces. Report what was measured, not what the test summary said.
- **Before reading failures as a regression**, cluster them by error signature — a failing suite is
  often the environment (MockLab down, partner domain down, a task missing from the system package).
  A long span with a gap is usually awaiting an episode traced under its own lane anchor; read the
  component definition before calling a duration a regression.
- **A server is unreachable → say so**, name the check that did not run, and label the result ("tests
  passed, not confirmed against traces"). Use the on-disk fallbacks; timing claims have no fallback.
  Bring the infra up yourself only when the user asked or the integration-test policy requires the run
  (`AGENTS.md` § Build, run, test), otherwise offer; never take down a stack owned by another compose
  file. `run-docker.sh up` does not start `openobserve` / `elasticsearch` — see the runbook. A server that failed at session start stays down for the session (`/mcp`).
- **OpenObserve:** org `default`, stream **`vnext`** (logs and traces), top-level `type` parameter,
  times in microseconds, message column `body`. `postgres` `CONNECT_TIMEOUT` usually means core's
  DbMigrator has not created `Aether_WorkflowDb` yet — list the databases before suspecting the server.

Full guide — traps, fallbacks, query mechanics, the `postgres` timeout:
[docs/testing/observability-verification.md](../../docs/testing/observability-verification.md).
