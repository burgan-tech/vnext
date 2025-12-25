---
name: TaskCoordinator Response Refactor
overview: TaskCoordinator'un ExecuteAsync metodunu Result<IEnumerable<StandardTaskResponse>> döndürecek şekilde değiştirip, TaskExecutionMiddleware'in hem Result hem de StatusCode bazlı retry/error boundary yapabilmesini sağlamak.
todos:
  - id: interface-update
    content: ITaskCoordinator.ExecuteAsync return type'ını Result<IReadOnlyList<StandardTaskResponse>> yap
    status: pending
  - id: coordinator-update
    content: TaskCoordinator.ExecuteAsync ve ExecuteTaskAsync'i StandardTaskResponse döndürecek şekilde güncelle
    status: pending
    dependencies:
      - interface-update
  - id: pipeline-factory
    content: ITaskResiliencePipelineFactory ve TaskResiliencePipelineFactory'ye CreateStandardResponsePipeline ekle
    status: pending
    dependencies:
      - interface-update
  - id: middleware-update
    content: TaskExecutionMiddleware'i yeni pipeline ve StatusCode kontrolü ile güncelle
    status: pending
    dependencies:
      - coordinator-update
      - pipeline-factory
---

# TaskCoordinator Response Refactoring

## Mimari Değişiklik

```mermaid
sequenceDiagram
    participant TEM as TaskExecutionMiddleware
    participant TC as TaskCoordinator
    participant TE as TaskExecutor
    participant RI as RemoteInvoker
    participant ES as External Service

    TEM->>TC: ExecuteAsync
    TC->>TE: ExecuteAsync
    TE->>RI: InvokeAsync
    RI->>ES: HTTP Call
    ES-->>RI: 503 Service Unavailable
    RI-->>TE: TaskInvocationResult (StatusCode=503)
    TE-->>TC: Result.Ok(StandardTaskResponse)
    TC-->>TEM: Result.Ok(StandardTaskResponse)
    Note over TEM: StatusCode=503 kontrolü
    Note over TEM: Polly Retry tetiklenir!
```



## Değişiklikler

### 1. ITaskCoordinator Interface Güncelleme

[`src/BBT.Workflow.Application/Tasks/ITaskCoordinator.cs`](src/BBT.Workflow.Application/Tasks/ITaskCoordinator.cs)

```csharp
// Mevcut:
Task<Result> ExecuteAsync(...);

// Yeni:
Task<Result<IReadOnlyList<StandardTaskResponse>>> ExecuteAsync(...);
```



### 2. TaskCoordinator Implementasyonu

[`src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs`](src/BBT.Workflow.Application/Tasks/Coordinator/TaskCoordinator.cs)

- `ExecuteAsync()` → `Result<IReadOnlyList<StandardTaskResponse>>` dönsün
- `ExecuteTaskAsync()` → `Result<StandardTaskResponse>` dönsün
- Response collection'ı biriktirip dönsün

### 3. TaskResiliencePipelineFactory Güncelleme

[`src/BBT.Workflow.Application/Tasks/Resilience/TaskResiliencePipelineFactory.cs`](src/BBT.Workflow.Application/Tasks/Resilience/TaskResiliencePipelineFactory.cs)Yeni metod ekle:

```csharp
ResiliencePipeline<Result<StandardTaskResponse>> CreateStandardResponsePipeline(
    string taskKey,
    RetryPolicy? errorBoundaryPolicy = null);
```

`ShouldRetry` predicate:

- `Result.IsSuccess == false` → retry
- `StandardTaskResponse.StatusCode` retryable ise → retry
- `StandardTaskResponse.IsSuccess == false` → retry

### 4. TaskExecutionMiddleware Güncelleme

[`src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionMiddleware.cs`](src/BBT.Workflow.Application/Tasks/Coordinator/TaskExecutionMiddleware.cs)

- `CreateStandardResponsePipeline()` kullan
- StatusCode ve IsSuccess kontrolü artık Polly içinde otomatik
- ErrorBoundary için `StandardTaskResponse` bilgilerini kullan

### 5. ITaskResiliencePipelineFactory Interface Güncelleme

[`src/BBT.Workflow.Application/Tasks/Resilience/ITaskResiliencePipelineFactory.cs`](src/BBT.Workflow.Application/Tasks/Resilience/ITaskResiliencePipelineFactory.cs)Yeni interface metodu ekle.

## Etkilenen Dosyalar