# Unit Tests: Always Green

The unit-test suite under `test/` (`Domain.Tests`, `Application.Tests`, `Infrastructure.Tests`) is
kept at **zero failures**, on master and on every pull request. There is no list of known reds,
no baseline and no "pre-existing failure" exception.

## The rule

- **Every PR leaves the suite green.** The `Unit Tests` check
  ([`pr-tests.yml`](../../.github/workflows/pr-tests.yml)) fails on any failing test, a crashed
  or hung test host included, and it is a required check on `master`.
- **A failure you did not cause is still yours to resolve before merge.** If a test turns red
  on master (a flaky test, an environment change, a merge that skipped CI), the next PR either
  fixes it or a fix PR lands first. Do not merge around a red check.
- **Fix the cause, not the assertion.** For each red decide whether the test is outdated (the
  behaviour changed on purpose — name the commit or doc) or the production code is wrong (then
  it is a bug fix with its own test). Never make a test pass by copying the current output into
  the expected value without that judgement.
- **No `Skip` to get green.** A skipped test needs a stated reason and an issue reference
  (`evidence/guard-debug-leftovers`). A test is deleted only when the behaviour it pinned was
  removed on purpose.
- **Flaky is red.** A test that passes locally and fails on CI (order dependence, shared
  database state, time, culture) is fixed to be deterministic, not retried.

## The check

| Step | What it does |
|---|---|
| Raise inotify limits | Linux runners default to 128 inotify instances; tests that build a host each add a config watcher. Without this step about 500 Domain tests fail with `inotify instances ... reached`. |
| Test | Builds `BBT.Workflow.slnx` (Release) and runs the three projects one after another, each to its own TRX. A project without a TRX (crash, hang) fails the check. |
| Report | [`scripts/ci/unit-tests-report.mjs`](../../scripts/ci/unit-tests-report.mjs) writes the job summary — totals per project and failures grouped by error signature — and fails on any failure. |
| Upload | `unit-test-results` artifact: the TRX files, `failing.json`, and blame sequence files for hangs. |

Reproduce locally exactly as CI does:

```bash
dotnet build BBT.Workflow.slnx -c Release
dotnet test test/BBT.Workflow.Domain.Tests -c Release --no-build
dotnet test test/BBT.Workflow.Application.Tests -c Release --no-build
dotnet test test/BBT.Workflow.Infrastructure.Tests -c Release --no-build
```

Run projects one at a time, not `dotnet test BBT.Workflow.slnx`: the solution-wide run executes the
projects in parallel and has historically produced extra failures that a per-project run does not.
`Infrastructure.Tests` needs Docker (Testcontainers Postgres).

## Emergency switch

The repository variable `UNIT_TESTS_MODE=report` stops the check from blocking — for an outage of the
runner image or the container registry, never for a failing test. Unset it as soon as the outage
ends; the check enforces when the variable is absent.
