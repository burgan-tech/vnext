# Spec 02: Runtime Execution

## Purpose

Document task execution from pipeline step to Orchestration executor to Execution invoker,
including scripting and remote task invocation boundaries.

## Deliverables

- [Task Executors and Invokers](../runtime/task-executors-and-invokers.md)
- [Script Context and Engine](../runtime/script-context-and-engine.md)

## Key Decisions

- Executors understand workflow context and task definitions.
- Invokers understand typed bindings and external protocols.
- Execution host stays stateless and does not mutate instance aggregates.
- Scripts run with an explicit context and helper surface.

## Acceptance Checklist

- Executor-vs-invoker distinction is explicit.
- Task envelope contract is referenced.
- Script context data and mutation behavior are documented.
- Failure mapping from task result to pipeline/error boundary is described.

## Source Alignment

Review these files when the spec changes:

- `src/BBT.Workflow.Application/Tasks/Executors/`
- `src/BBT.Workflow.Execution.Abstractions/TaskEnvelope.cs`
- `src/BBT.Workflow.Execution/Invokers/`
- `modules/BBT.Workflow.Modules.Scripting/`

