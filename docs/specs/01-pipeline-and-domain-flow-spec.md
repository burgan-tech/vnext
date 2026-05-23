# Spec 01: Pipeline and Domain Flow

## Purpose

Document the transition pipeline and domain-facing instance lifecycle so engineers can
change workflow behavior without reverse-engineering every step from source.

## Deliverables

- [Workflow Execution Pipeline](../architecture/workflow-execution-pipeline.md)
- [Instance Data Merge Concept](../domain/instance-data-merge-concept.md)
- [Domain Cache Context](../domain/domain-cache-context.md)
- [Function Handler Architecture](../domain/function-handler-architecture.md)

## Key Decisions

- Pipeline order is defined by `LifecycleOrder` and should stay deterministic.
- Trigger-specific profiles remove steps instead of scattering `if trigger` checks across steps.
- `TransitionExecutionContext` is service-free and carries data, definitions, headers,
  telemetry identifiers, directives, and per-transition cache.
- `DomainCacheContext` is the definition cache boundary; it is Redis-first, typed by
  component, and backed by runtime storage on cache misses.
- Function handlers translate HTTP shape into application inputs; they do not own domain decisions.

## Acceptance Checklist

- Step order and profile exclusions are documented.
- Stop, skip, finalize, and directive behavior are explained.
- Sync, async, auto-chain, scheduled, event, error-boundary, and subflow paths are covered at a high level.
- Instance data versioning and ETag behavior are connected to client polling.
- Definition cache reads, writes, version keys, fallback behavior, and metrics are documented.

## Source Alignment

Review these files when the spec changes:

- `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs`
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`
- `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs`
- `src/BBT.Workflow.Application/Caching/DomainCacheContext.cs`
- `src/BBT.Workflow.Application/Caching/CacheSet.cs`
- `src/BBT.Workflow.Application/Caching/ComponentCacheStore.cs`
- `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/Functions/Handlers/`
