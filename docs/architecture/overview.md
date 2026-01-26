# Architecture Overview

## System Architecture

The BBT Workflow Engine follows **Clean Architecture** principles with **Domain-Driven Design (DDD)** patterns, now implemented as a **microservices architecture**. The system is organized into four distinct layers with two separate API services for better scalability and separation of concerns.

```
┌─────────────────────────────────────────────────────────┐
│                 Presentation Layer                      │
│              Two Separate API Services                  │
│  ┌─────────────────────┐  ┌─────────────────────────┐  │
│  │  Orchestration API  │  │    Execution API        │  │
│  │      (Public)       │  │     (Internal)          │  │
│  │ ┌─────────────────┐ │  │ ┌─────────────────────┐ │  │
│  │ │   Controllers   │ │  │ │    Controllers      │ │  │
│  │ │   Middlewares   │ │  │ │    Middlewares      │ │  │
│  │ │    Filters      │ │  │ │     Filters         │ │  │
│  │ └─────────────────┘ │  │ └─────────────────────┘ │  │
│  └─────────────────────┘  └─────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│                  Application Layer                      │
│               BBT.Workflow.Application                  │
│ ┌─────────────┐ ┌─────────────┐ ┌─────────────────────┐ │
│ │ Orchestration│ │  Execution  │ │      SubFlow        │ │
│ │   Services   │ │   Services  │ │     Services        │ │
│ └─────────────┘ └─────────────┘ └─────────────────────┘ │
│ ┌─────────────┐ ┌─────────────┐ ┌─────────────────────┐ │
│ │  Extensions │ │ Persistence │ │     Instances       │ │
│ │   Services  │ │ Strategies  │ │     Services        │ │
│ └─────────────┘ └─────────────┘ └─────────────────────┘ │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│                   Domain Layer                          │
│                BBT.Workflow.Domain                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐    │
│  │  Entities   │  │  Aggregates │  │   Services  │    │
│  └─────────────┘  └─────────────┘  └─────────────┘    │
└─────────────────────────────────────────────────────────┘
                           │
┌─────────────────────────────────────────────────────────┐
│                Infrastructure Layer                     │
│             BBT.Workflow.Infrastructure                 │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐    │
│  │ Repositories│  │   EF Core   │  │   Cache     │    │
│  └─────────────┘  └─────────────┘  └─────────────┘    │
└─────────────────────────────────────────────────────────┘
```

## Microservices Architecture

### Service Separation Strategy

The system is divided into two specialized API services:

#### 1. **Orchestration API** (`BBT.Workflow.Orchestration.HttpApi.Host`)
- **Scope**: Public-facing API for external clients
- **Port**: 4201 (HTTP), 7189 (HTTPS)
- **Responsibilities**:
  - Workflow definition and management
  - Instance lifecycle management
  - Client authentication and authorization
  - API gateway functionality
  - Cross-cutting concerns (logging, monitoring)

#### 2. **Execution API** (`BBT.Workflow.Execution.HttpApi.Host`) 
- **Scope**: Internal API for task processing
- **Port**: 4202 (HTTP), 7190 (HTTPS)
- **Responsibilities**:
  - Task execution and processing
  - Background job management
  - Task-specific operations
  - Performance-optimized task handling

### Communication Pattern

```
[External Clients] → [Orchestration API] → [Execution API]
                                    ↓
                              [Shared Domain & Infrastructure]
```

The Orchestration API acts as the primary entry point and delegates task execution to the Execution API when needed.

## Layer Responsibilities

### 1. Domain Layer (`BBT.Workflow.Domain`)

The **innermost layer** containing business logic and rules. It has no dependencies on external frameworks and is shared by both API services.

**Key Components:**
- **Entities**: Core business objects (`Instance`, `Workflow`, `WorkflowTask`)
- **Aggregates**: Consistency boundaries (`Instance` as aggregate root)
- **Domain Services**: Business logic that doesn't belong to a single entity
- **Value Objects**: Immutable objects (`JsonData`, `Reference`)
- **Domain Events**: Business events for decoupling

**Example Entity:**
```csharp
public sealed class Instance : AggregateRoot<Guid>, IHasCreatedAt, IHasModifyTime
{
    public string? Key { get; private set; }
    public string Flow { get; private set; }
    public string? CurrentState { get; private set; }
    public InstanceStatus Status { get; private set; }
    
    public static Instance Create(Guid id, string flow, string? key = null)
    {
        return new Instance(id, flow, key);
    }
    
    public void AddDataWithVersion(Guid id, JsonData jsonData, string? version = null)
    {
        // Business logic implementation
    }
}
```

### 2. Application Layer (`BBT.Workflow.Application`)

Orchestrates domain objects to fulfill use cases. Contains application-specific business rules organized into specialized modules, shared by both API services:

#### **Orchestration Module (`BBT.Workflow.Orchestration`)**
- **TaskOrchestrationService**: Coordinates task execution with parallel/sequential strategies
- **ITaskOrchestrator**: Abstraction for task orchestration (local/remote)
- **Primary Usage**: Orchestration API for workflow coordination

#### **Execution Module**
- **Transition Pipeline (`BBT.Workflow.Execution.Pipeline`)**: Deterministic transition lifecycle execution
  - **TransitionPipeline**: Orchestrates step-by-step transition execution with dynamic plan building
  - **ITransitionStep**: Pipeline step abstraction with lifecycle order, returns `Result<StepOutcome>`
  - **Pipeline Steps**: ForwardToActiveSubflow, CreateTransition, OnExecute, OnExit, ChangeState, OnEntry, SubFlow, ClearBusyOnResume, Schedule, Auto, HandleFinish, Finalize, ProcessInlineAutoChain
  - **PipelineDirectives**: Runtime control of pipeline behavior (skip, resume, stop, epilogue modes)
  - **StepOutcome**: Step result handling with flow control (Continue, Stop, SkipTo, MutateDirectives)
- **Execution Strategies (`BBT.Workflow.Execution.Strategies`)**: Strategy pattern for sync/async execution modes
  - **SyncTransitionStrategy**: Immediate execution with pipeline
  - **AsyncTransitionStrategy**: Background job scheduling
  - **IExecutionStrategyFactory**: Factory for strategy resolution
- **Transition Handlers (`BBT.Workflow.Execution.Handlers`)**: Trigger-specific pre/post processing
  - **ManualTransitionHandler**: User-initiated transitions with auth/validation
  - **AutomaticTransitionHandler**: Condition-based automatic transitions
  - **ScheduledTransitionHandler**: Timer-based transitions
  - **EventTransitionHandler**: Event-driven transitions
- **Re-entry System (`BBT.Workflow.Execution.ReEntry`)**: Inline and background job dispatch for Auto/Schedule transitions
  - **IReentryDispatcher**: Manages re-entry execution (inline vs background)
  - **ReentryCommand**: Command object for re-entry transitions
- **Context Management (`BBT.Workflow.Execution.Context`)**: Context factories and refreshers
  - **ITransitionContextFactory**: Creates `TransitionExecutionContext` from `WorkflowExecutionContext`
  - **IContextRefresher**: Refreshes context state during execution
- **Task Execution (`BBT.Workflow.Execution.Tasks`)**: Task executor implementations
- **Primary Usage**: Execution API for transition and task processing

#### **SubFlow Module (`BBT.Workflow.SubFlow`)**
- **SubFlowService**: SubFlow and SubProcess workflow management
- **InstanceCorrelation**: Parent-child workflow relationships

#### **Extensions Module (`BBT.Workflow.Extensions`)**
- **InstanceExtensionService**: Instance extension operations

#### **Persistence Module (`BBT.Workflow.Tasks.Persistence`)**
- **Task Persistence Strategies**: Pluggable persistence strategies for different task types
- **TaskPersistenceStrategyFactory**: Factory for creating appropriate persistence strategies

#### **Instances Module**
- **Core Instance Services**: Basic instance management operations (`IInstanceAppService`, `IAdminAppService`)
- **DTOs**: Data transfer objects for API communication

**Example Service:**
```csharp
namespace BBT.Workflow.Orchestration;

public sealed class TaskOrchestrationService : ITaskOrchestrationService
{
    public async Task ExecuteAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        InstanceTransition? instanceTransition,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Orchestrate task execution based on dependencies
        var canExecuteInParallel = CanExecuteInParallel(tasks);
        
        if (canExecuteInParallel)
        {
            await ExecuteTasksInParallelAsync(tasks, instanceTransition, taskTrigger, context, cancellationToken);
        }
        else
        {
            await ExecuteTasksSequentiallyAsync(tasks, instanceTransition, taskTrigger, context, cancellationToken);
        }
    }
}
```

### 3. Infrastructure Layer (`BBT.Workflow.Infrastructure`)

Implements technical concerns and external system integrations, shared by both API services.

**Key Components:**
- **Repositories**: Data access implementations using Entity Framework Core
- **External Services**: DAPR integrations, HTTP clients
- **Caching**: Redis-based caching implementations
- **Background Jobs**: DAPR job scheduling

**Example Repository:**
```csharp
public sealed class EfCoreInstanceRepository : EfCoreRepository<WorkflowDbContext, Instance, Guid>, IInstanceRepository
{
    public async Task<Instance?> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        return await (await GetDbSetAsync())
            .Include(i => i.DataList)
            .FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
    }
}
```

### 4. Presentation Layer (Dual API Architecture)

#### **Orchestration API** (`BBT.Workflow.Orchestration.HttpApi.Host`)

Handles client-facing HTTP requests and workflow management.

**Key Components:**
- **Controllers**: Client-facing REST API endpoints
- **Middlewares**: Authentication, authorization, logging
- **Filters**: Request/response processing
- **Configuration**: Client API setup and DI registration

**Example Controller:**
```csharp
[ApiController]
[Route("api/[controller]")]
public class WorkflowController : ControllerBase
{
    private readonly IWorkflowAppService _workflowService;
    
    [HttpPost]
    public async Task<IActionResult> CreateWorkflow([FromBody] CreateWorkflowDto dto)
    {
        var result = await _workflowService.CreateAsync(dto);
        return Ok(result);
    }
}
```

#### **Execution API** (`BBT.Workflow.Execution.HttpApi.Host`)

Handles internal task execution and processing.

**Key Components:**
- **Controllers**: Internal task execution endpoints
- **Middlewares**: Performance monitoring, error handling
- **Filters**: Task-specific processing
- **Configuration**: Execution API setup and DI registration

**Example Controller:**
```csharp
[ApiController]
[Route("api/internal/[controller]")]
public class TaskExecutionController : ControllerBase
{
    private readonly ITaskExecutionService _executionService;
    
    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteTask([FromBody] ExecuteTaskDto dto)
    {
        await _executionService.ExecuteAsync(dto);
        return Ok();
    }
}
```

## Modular Design Patterns

### 1. Separation of Concerns

Each service and module has a clear responsibility:

- **Orchestration API**: Client interaction and workflow coordination
- **Execution API**: Task processing and execution
- **Orchestration Module**: Coordinates task execution strategies
- **Execution Module**: Manages state transitions and rule evaluation
- **SubFlow Module**: Handles parent-child workflow relationships
- **Extensions Module**: Manages instance extensions
- **Persistence Module**: Handles different task persistence strategies

### 2. Repository Pattern

Abstraction over data access with clean interfaces:
```csharp
public interface IInstanceRepository : IRepository<Instance, Guid>
{
    Task<Instance?> FindByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<Instance> GetActiveAsync(Guid id, CancellationToken cancellationToken = default);
}
```

### 3. Factory Pattern

Task executor creation based on task type:
```csharp
public interface ITaskExecutorFactory
{
    ITaskExecutor GetExecutor(TaskType type);
}
```

## Transition Pipeline Architecture

The execution layer uses a **pipeline-based architecture** for managing workflow state transitions. This provides a deterministic, extensible, and observable execution model with exception-free error handling using the Result pattern.

### Core Concepts

#### 1. Pipeline Steps
Each transition goes through a series of ordered steps:
```
ForwardToActiveSubflow (5) → CreateTransition (10) → OnExecute (20) → OnExit (30) → 
ChangeState (40) → OnEntry (50) → SubFlow (60) → ClearBusyOnResume (69) → 
Schedule (70) → Auto (80) → HandleFinish (90) → Finalize (100) → ProcessInlineAutoChain (101)
```

#### 2. Dynamic Plan Building
The `TransitionPipeline` dynamically builds execution plans using the `BuildExecutionPlan` method based on:
- **Resume Points**: Start from specific step (e.g., after SubFlow completion)
- **Epilogue Mode**: Skip/Run for Schedule/Auto steps
- **Terminal States**: Short-circuit to Finalize on workflow completion
- **Directive Changes**: Rebuild plan mid-execution based on runtime conditions

Planning logic is integrated directly into the pipeline for better performance and simpler architecture.

#### 3. Result Pattern & Step Outcomes
Each step returns `Result<StepOutcome>` for exception-free error handling. `StepOutcome` can:
- **Continue**: Proceed to next step
- **Stop**: Stop pipeline completely
- **SkipTo**: Jump to specific order (e.g., restart from CreateTransition)
- **MutateDirectives**: Change pipeline behavior (e.g., skip epilogue)

```csharp
// Success case
return Result<StepOutcome>.Ok(StepOutcome.Continue());

// Error case
return Result<StepOutcome>.Fail(Error.Validation("code", "message"));
```

#### 4. Trigger Handlers
Pre/Post processing based on how transition was triggered:
- **Manual**: User authentication, authorization, audit logging
- **Automatic**: Condition validation, chain depth limits, inline execution
- **Scheduled**: Timer validation, recurring schedules
- **Event**: Event source validation, correlation

#### 5. Re-entry System
Auto and Schedule transitions can be executed:
- **Inline**: Within same request for immediate transitions
- **Background**: Via Dapr jobs for delayed/scheduled transitions

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                   Transition Request                        │
│            (Manual/Auto/Schedule/Event)                     │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│              Transition Strategy                            │
│           (Sync/Async Mode Selection)                       │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│            Trigger Handler (PreHandle)                      │
│   • Validation  • Auth  • Logging                          │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│              Pipeline Planner                               │
│   Build execution plan based on directives                 │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│          Transition Pipeline Execution                      │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  For each step in plan:                              │  │
│  │  1. Execute step                                     │  │
│  │  2. Check StepOutcome (Continue/Stop/Skip)          │  │
│  │  3. Apply directive mutations                        │  │
│  │  4. Re-plan if needed                                │  │
│  └──────────────────────────────────────────────────────┘  │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│            Trigger Handler (PostHandle)                     │
│   • Cleanup  • Final Validation  • Metrics                 │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                  Transition Complete                        │
└─────────────────────────────────────────────────────────────┘
```

### Key Benefits

1. **Deterministic Execution**: Clearly defined step order and lifecycle
2. **Extensibility**: Add new steps without modifying existing code
3. **Testability**: Each step and planner can be tested independently
4. **Observability**: Detailed telemetry for each step execution
5. **Flexibility**: Dynamic re-planning based on runtime conditions
6. **Separation of Concerns**: Trigger logic, planning, and execution are decoupled

For detailed documentation, see [Transition Pipeline Architecture](./transition-pipeline-architecture.md).

## Benefits of the Microservices Architecture

### 1. **Scalability**
- Each API can be scaled independently based on demand
- Execution API can handle more instances for CPU-intensive tasks
- Orchestration API can be optimized for client interactions

### 2. **Separation of Concerns**
- Clear boundaries between client-facing and internal operations
- Better security isolation
- Easier maintenance and development

### 3. **Technology Flexibility**
- Each service can be optimized for its specific use case
- Different deployment strategies per service
- Independent release cycles

### 4. **Performance Optimization**
- Execution API optimized for throughput and processing
- Orchestration API optimized for client experience
- Better resource utilization

## Inbox/Outbox Workers

The system includes dedicated worker services for reliable event processing:

### Workers Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                      Worker Services                             │
│  ┌─────────────────────────┐  ┌─────────────────────────────┐   │
│  │  BBT.Workflow.Workers   │  │  BBT.Workflow.Workers       │   │
│  │       .Inbox            │  │       .Outbox               │   │
│  │                         │  │                             │   │
│  │ • InboxProcessorHosted  │  │ • OutboxProcessorHosted     │   │
│  │   Service               │  │   Service                   │   │
│  │ • Event Handlers        │  │ • IOutboxProcessor          │   │
│  │ • IInboxProcessor       │  │ • Dapr PubSub Publishing    │   │
│  └─────────────────────────┘  └─────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

### Inbox Worker

Processes incoming events from the message broker:

- `InstanceSubCompletedEventHandler` - Handles subflow completion
- `InstanceCanceledEventHandler` - Handles instance cancellation
- `ChildSubflowCancelRequestedEventHandler` - Handles child cancellation requests

### Outbox Worker

Publishes pending events from the outbox table:

- Polls outbox table for pending messages
- Publishes to Dapr PubSub
- Handles retry logic and dead-letter scenarios

For detailed documentation, see [Inbox/Outbox Workers](./inbox-outbox-workers.md).

## Aether SDK Integration

The system leverages Aether SDK for cross-cutting concerns:

### Aspects

| Aspect | Purpose |
|--------|---------|
| `[UnitOfWork]` | Transaction management |
| `[Log]` | Structured logging |
| `[Trace]` | OpenTelemetry distributed tracing |
| `[Enrich]` | Log context enrichment |
| `[AutoUnitOfWork]` | Assembly-level automatic UoW |

### Result Pattern

All business operations use `Result<T>` for exception-free error handling with Railway Programming extensions:

- `BindAsync` - Chain async Result operations
- `MapAsync` - Transform success values
- `ThenAsync` - Chain sync operations
- `Tap/TapAsync` - Side effects without changing Result
- `OnSuccess` - Execute on success only

### Example Usage

```csharp
[UnitOfWork]
[Log]
[Trace]
public Task<Result<TransitionOutput>> ExecuteTransitionAsync(
    [Enrich] WorkflowExecutionContext context,
    CancellationToken cancellationToken = default)
{
    return GetExecutionStrategy(context.Mode)
        .BindAsync(strategy => ExecuteStrategyAsync(strategy, context, cancellationToken))
        .BindAsync(execCtx => BuildTransitionOutputAsync(context, execCtx, cancellationToken));
}
```

For detailed documentation, see:
- [Aether SDK Aspects](./aether-sdk-aspects.md)
- [Result Pattern & Railway Programming](./result-pattern-railway.md)

## Deployment Considerations

### Development Environment
Both APIs and Workers run simultaneously using Docker Compose for local development.

### Production Environment
- Deploy APIs as separate services
- Deploy Workers as separate services with appropriate replicas
- Use service discovery for internal communication
- Implement circuit breakers and retry policies
- Monitor each service independently 