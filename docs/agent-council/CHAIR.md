# Council Chair

## Identity

- Name/actor ID: **Mehmet TOSUN** — `mtosun@burgantech.com` (GitHub `middt`)
- Backup/delegate: none configured — a High/Critical decision closes as `Awaiting Chair` while the Chair is unavailable
- Authority source: vNext runtime repository owner / platform team lead (standing, not delegated)
- Delegation expiry (UTC): none — standing appointment; revisit when a backup is named

The configured Chair protects architectural boundaries, risk controls, evidence quality, and long-term maintainability. The Chair need not design the solution.

## Mandatory Approval

Service boundary or data ownership changes, new infrastructure, authentication/authorization/token/secret changes, breaking cross-service contracts, production migrations, high-blast-radius changes, critical technical debt, and unresolved hard constraints.

## Decisions

`APPROVE`, `APPROVE_WITH_CONDITIONS`, `REQUEST_REVISION`, `REQUEST_EXPERIMENT`, `VETO`.

## How this Chair decides — standing precedents

Facilitators and role agents should read these before drafting `DECISION.md`. They summarize decisions
the Chair has already made in this repository; a proposal that contradicts one must say so explicitly
and argue why the precedent should change. It is not re-litigated silently.

### Review and release gates

- **Nothing is committed, pushed, or opened on GitHub without an explicit instruction.** Implementation
  plus build plus tests is delivered as a diff summary; the commit is the Chair's call. Issue and PR
  drafts are produced as local markdown first; the `gh` CLI is not run proactively, not even read-only.
- **Unit tests are not enough for a pipeline-level change.** Anything touching transition pipeline,
  subflow lifecycle, locking, instance data, error boundary or the state function needs an integration
  test in `vnext-example` against the **locally built** runtime, never against a container image.
  Starting infrastructure or the four hosts is an expensive step and is approved case by case.
- **Aether is proposed to, not edited.** A change that is better made in the SDK than as a permanent
  vNext workaround is written up as a proposal with the trade-off; the Chair decides whether to take it
  to Aether.
- **Evidence before claims.** Performance, recovery and compatibility statements need a measurement or a
  reproducible test (precedent: the layered script-perf lab, the gRPC proxy-mode E2E report, the
  write-path perf cherry-pick report — dated design records). An unmeasured claim yields
  `REQUEST_EXPERIMENT`, not approval.

### Architectural decisions already taken (do not reopen without new evidence)

- **`transition.roles` is not enforced at `POST .../transitions/{key}`.** Roles describe what a client
  should offer; real boundaries live in `queryRoles`, function `roles` or task logic.
- **Only `updateData` skips the state lifecycle on a `$self` target.** Every other `$self` transition runs
  the full OnExit/OnEntry/Schedule lifecycle. No literal-target-equals-current-state comparison.
- **The Busy flag is the mutex; one millisecond-scale status lock per hop.** No whole-chain lock, no
  per-kind lock keys. `updateData` takes no lock and no duplicate-job guard.
- **Auto → Schedule epilogue order.** When the auto step picks a winner, no timer is armed for that hop.
- **Runtime-generated child calls (start, forward, descended retry) are always `sync=true`**;
  `S`/`P` decides parent continuation, not transport mode. Responses are identity-only.
- **A parent with an open SubFlow correlation stays Busy for the child's lifetime**; the client observes
  the leaf via the state function.
- **Subflow terminal events are the only dual-delivery events** (outbox + post-commit relay, backup
  deduplicated by `ISubItemTerminalGuard`). Every other distributed event has exactly one handler; the
  EventHook infrastructure stays deleted.
- **Scheduled-transition job changes do not participate in the state-function ETag** (issue #864);
  documented as a known gap, not a bug to fix.
- **Activation episode tracing**: one backdated `Instance.Activation/{key}` span per trigger→rest-point
  episode, emitted after commit, kind `Internal`; timers stay separate traces; the parent episode ends
  at the subflow handoff. Every new lane carrier copies all three episode fields.
- **AI guidance has one source.** Rules and skills live under `.claude/`; Cursor reaches them through
  `@` pointers; `AGENTS.md` is the bootstrap for every agent. Runtime facts are linked, never copied.
- **Decision history is recorded.** Dated specs, plans, reports and council sessions live in local
  scratch (`ai-docs/`, git-ignored, layout in `AGENTS.md`); every council decision gets a committed row in
  the decision log `docs/agent-council/sessions/README.md`.

### What the Chair expects from a decision record

- Verified facts separated from assumptions; code (`LifecycleOrder.cs`, `PipelineExecutionProfile.cs`,
  `PipelineProfileResolver.cs`) cited over docs when they disagree.
- A real dissent table. A council where nobody argued the losing side is sent back.
- A rollback path and an integration-test plan per the policy above, or an explicit statement of why
  the change is below that threshold.
- For anything adding a component type: the coordination cost with the external
  `@burgan-tech/vnext-schema` package and `vnext-meta`, and the Helm chart impact of any new
  required configuration.

## Record

- Decision:
- Rationale:
- Mandatory conditions:
- Condition owner:
- Required evidence:
- Due before (UTC):
- Accepted risks:
- Review date:
