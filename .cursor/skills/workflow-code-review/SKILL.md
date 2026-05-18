---
name: workflow-code-review
description: Systematic code review for vNext workflow pipeline changes. Checks pipeline step discipline, repository include efficiency, long-polling correctness, error boundary integrity, result pattern compliance, and clean architecture. Use when the user says "code review", "review et", "incele", "review yap", or asks to review pipeline, transition, or workflow code.
disable-model-invocation: true
---

# Workflow Code Review

## Trigger

Activate when the user requests a code review of workflow-related code: "code review yap", "review et", "incele", "review this change".

## Workflow

1. Identify changed files via `git diff` or staged changes
2. Classify each change by which checklist sections apply
3. Run through **all applicable sections** below
4. Report findings grouped by severity

## Severity Levels

- **CRITICAL**: Must fix — correctness, data loss, or security risk
- **WARNING**: Should fix — performance, maintainability, or contract violation
- **INFO**: Nice to have — style, readability, minor improvement

---

## 1. Pipeline Step Discipline

- [ ] Step has a single responsibility — does not mix concerns (e.g. state change + task execution)
- [ ] Step order constant is defined in `LifecycleOrder` and matches the pipeline contract:
  5→9→10→19→20→25→30→38→39→40→50→60→70→79→80→90→100→110→112
- [ ] `SkipTo(order)` targets an existing `LifecycleOrder` constant
- [ ] `SkipToFinalize()` is used instead of hardcoded `SkipTo(110)`
- [ ] Step returns `Result<StepOutcome>` — not raw exceptions
- [ ] `With(Action<PipelineDirectives>)` mutations are minimal and well-documented
- [ ] New steps are registered in `PipelineServiceCollectionExtensions`
- [ ] `PipelineExecutionProfile` exclusion lists updated if the new step should be skipped for certain trigger types

## 2. Repository / Include Efficiency

- [ ] No navigation properties included that this step does not consume
- [ ] No re-query when `TransitionExecutionContext` already holds the data
- [ ] `AsNoTracking()` used on read-only / monitoring paths
- [ ] No N+1 patterns: check for loops calling repository methods
- [ ] `WithDetailsAsync()` is not called with additional includes unless justified
- [ ] History loads use `AsNoTracking` + explicit filtered includes
- [ ] Paged queries include `DataList` only when data is needed in response

## 3. Long-Polling / State Function Correctness

- [ ] State response transitions are filtered via `ITransitionAuthorizationManager`
- [ ] ETag generation uses `IRepresentationEtagService.Generate(output)` — not manual hashing
- [ ] `IfNoneMatch` / `304 Not Modified` flow is preserved
- [ ] Subflow completion window handled: parent transitions shown while correlation is open
- [ ] No state mutation in read-only State function path
- [ ] `effectiveState` and `effectiveStateSubType` correctly reflect the actual runtime state

## 4. Error Boundary Integrity

- [ ] `CompiledBoundaryChain` resolution order respected: Task → State → Global
- [ ] Rule priority is explicit — no implicit ordering assumptions
- [ ] Wildcard/fallback rule exists (low priority) to prevent unhandled errors
- [ ] `BoundaryOutcomeHandler` mapping verified:
  - `Log`/`Ignore` → `Continue()`
  - Transition set → `RequestNextTransition` + `SkipToFinalize()`
  - Abort without transition → `Fail` → fault
- [ ] Error-boundary profile used (`AllowAutoChain=false`, `AllowSubFlow=false`)
- [ ] `IsErrorBoundaryTransition` flag set on chained context
- [ ] Retry policy specifies `maxRetries` and backoff — no infinite retry loops

## 5. Result Pattern Compliance

- [ ] No `throw` for business logic errors — use `Result.Fail(error)`
- [ ] Steps return `Result<StepOutcome>.Fail()` — not `StepOutcome.Stop()`
- [ ] Railway pattern followed: `result.Then(...)` / `result.Map(...)` chains
- [ ] Error results include structured error codes, not just messages
- [ ] `Result.Ok()` with meaningful payload — no empty success results where data is expected

## 6. SubFlow Lifecycle

- [ ] SubFlow (S) completion resumes parent pipeline from `ClearBusyOnResumeStep` (order 79)
- [ ] SubProcess (P) completion does NOT resume parent — fire-and-forget
- [ ] Output mapping script runs before parent resume (SubFlow only)
- [ ] `StrictIdempotency: true` on `StartSubAsync` call
- [ ] Correlation revert on resume failure uses a new UoW
- [ ] Parent metadata in `ExtraProperties` is complete (parentId, parentKey, domain, flow, version, state, transition, flowType)

## 7. TransitionExecutionContext Usage

- [ ] Context not mutated outside pipeline steps (no side-channel writes)
- [ ] `Cache` entries cleaned at Finalize — no memory leaks
- [ ] `Directives` mutations only via `With(Action<PipelineDirectives>)` or direct step responsibility
- [ ] `Data` overlay from request payload handled correctly (no clobbering existing data)
- [ ] `ScriptContext` built via `GetOrBuildScriptContextAsync` — not manually constructed

## 8. PipelineExecutionProfile

- [ ] Profile resolver (`IPipelineProfileResolver`) returns correct profile for trigger type
- [ ] New steps excluded from profiles where they should not run (e.g. AutoChain skips SetBusy, CreateTransition)
- [ ] `AllowAutoChain` and `AllowSubFlow` flags respected in relevant steps

## 9. Logging & Observability

- [ ] Uses `WorkflowLogs.cs` extension methods — no raw `logger.Log*` calls
- [ ] Structured parameters include: `instanceId`, `transitionKey`, `flow`, `domain`
- [ ] New log events have unique EventIds following existing patterns
- [ ] Error logs include exception object — not just message string
- [ ] Activity/span created for major operations (OpenTelemetry)

## 10. General Clean Architecture

- [ ] No business logic in controllers, constructors, or infrastructure layer
- [ ] SOLID violations: SRP (step doing too much), OCP (switch on type instead of polymorphism), DIP (concrete dependency instead of interface)
- [ ] No unnecessary DI registrations — only inject what is used
- [ ] `async/await` used throughout — no `.Result` or `.Wait()` blocking calls
- [ ] Domain layer has no infrastructure dependencies
- [ ] DTOs do not leak EF entities or navigation properties

---

## Output Format

```markdown
## Code Review Summary

**Files reviewed**: [list]
**Overall risk**: Low / Medium / High

### CRITICAL
- [file:line] Description of issue

### WARNING
- [file:line] Description of issue

### INFO
- [file:line] Description of issue

### Positive observations
- What was done well
```
