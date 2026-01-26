# Domain Models

## Overview

The domain layer contains the core business logic and entities that represent the workflow system's domain concepts. These models are framework-agnostic and encapsulate the essential business rules and behaviors.

### Domain Areas: Definitions vs Instances

Within `BBT.Workflow.Domain`, the domain is organized around two primary areas:

- **Definitions**: Contains domain definitions and configuration models (workflow structure, tasks, transitions). These models are **not persisted directly** to the database.
- **Instances**: Contains runtime execution state and audit entities (instances, transitions, tasks). These models **have database persistence** and represent operational state.

## Core Entities and Aggregates

### 1. Instance (Aggregate Root)

The `Instance` represents a running workflow instance and serves as the main aggregate root.

```csharp
public sealed class Instance : AggregateRoot<Guid>, IHasCreatedAt, IHasModifyTime, IHasExtraProperties
{
    public string? Key { get; private set; }
    public string Flow { get; private set; }
    public string? CurrentState { get; private set; }
    public string? EffectiveState { get; private set; }
    public InstanceStatus Status { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public List<string> Tags { get; private set; }
    public ExtraPropertyDictionary ExtraProperties { get; private set; }
}
```

**Key Properties:**
- `Key`: Optional business identifier for the instance
- `Flow`: Workflow definition key
- `CurrentState`: Engine-internal state key
- `EffectiveState`: External-facing state (includes SubFlow state propagation)
- `Status`: Instance status (Active, Busy, Completed, Faulted, Passive)
- `DataList`: Versioned instance data records
- `ChildCorrelations`: SubFlow/SubProcess relationships

**Business Rules:**
- Flow key is mandatory and validated
- Key length and state keys are validated via domain constants
- State changes update `EffectiveState` and emit SubFlow state-change events
- Data versions are immutable once created

**Usage Example:**
```csharp
var instance = Instance.Create(
    GuidGenerator.Create(),
    "customer-onboarding",
    "CUST-12345");

instance.AddData(
    GuidGenerator.Create(),
    new JsonData(customerData),
    VersionStrategy.IncreasePatch);
```

### 2. InstanceData (Entity)

Represents versioned data associated with a workflow instance.

```csharp
public sealed class InstanceData : Entity<Guid>, IHasVersion, IHasEtag
{
    public Guid InstanceId { get; private set; }
    public string Version { get; private set; }
    public long VersionNo { get; internal set; }
    public bool IsLatest { get; private set; }
    public string DataHash { get; private set; }
    public string ETag { get; private set; }
    public JsonData Data { get; private set; }
    public DateTime EnteredAt { get; private set; }
    public dynamic? Attributes => Data.JsonElement.ToDynamic();
}
```

**Key Features:**
- **Semantic versioning** with version strategies (major/minor/patch)
- **Immutability** of historical data records
- **Latest pointer** via `IsLatest` (managed by database trigger)
- **Concurrency support** via `ETag` and instance-level `VersionNo`
- **Data hashing** for change detection

**Versioning Strategy:**
```csharp
internal InstanceData NewVersion(
    Guid id,
    JsonData jsonData,
    VersionStrategy versionStrategy,
    int historySequence)
{
    var newVersion = IncrementVersion(Version, versionStrategy);
    var newData = Data.Merge(jsonData);
    return new InstanceData(id, InstanceId, newVersion, newData, true, historySequence);
}
```

### 3. InstanceTransition (Entity)

Represents a state transition within a workflow instance.

```csharp
public sealed class InstanceTransition : Entity<Guid>
{
    public Guid InstanceId { get; private set; }
    public string TransitionId { get; private set; }
    public string FromState { get; private set; }
    public string? ToState { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public JsonData Body { get; private set; }
    public JsonData Header { get; private set; }
}
```

**Lifecycle Management:**
- Tracks transition execution timeline and duration
- Persists request payload (`Body`) and headers (`Header`)

### 4. InstanceTask (Entity)

Represents task execution within a workflow transition.

`Status` tracks platform execution, while `BusinessStatus` reflects the task's business outcome (e.g., HTTP 4xx/5xx).

```csharp
public sealed class InstanceTask : Entity<Guid>
{
    public Guid TransitionId { get; private set; }
    public string TaskId { get; private set; }
    public TaskStatus Status { get; private set; }
    public BusinessStatus BusinessStatus { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public JsonData Request { get; private set; }
    public JsonData Response { get; private set; }
}
```

**Status Management:**
```csharp
public void Completed(JsonData response, bool isBusinessSuccess)
{
    FinishedAt = DateTime.UtcNow;
    Status = TaskStatus.Completed;
    Response = response;
    BusinessStatus = isBusinessSuccess ? BusinessStatus.Success : BusinessStatus.Failed;
}

public void Faulted(string reason)
{
    FinishedAt = DateTime.UtcNow;
    Status = TaskStatus.Faulted;
    Response = new JsonData(JsonSerializer.Serialize(new { error = reason }));
}
```

### 5. InstanceCorrelation (Entity)

Tracks parent-child relationships between a parent instance and SubFlow/SubProcess executions, including the child flow identity and state synchronization fields.

### 6. InstanceJob (Entity)

Represents background processing jobs tied to an instance (e.g., scheduled work), with lifecycle tracking via `IsActive`, `CreatedAt`, and `ModifiedAt`.

## Workflow Definition Models (Definitions)

Definition models describe workflow structure and behavior. They are stored and resolved via definition sources, and are not persisted as runtime instance records.

### 1. Workflow (Aggregate Root)

Defines the structure and behavior of a workflow.

```csharp
public sealed class Workflow : IDomainEntity, IReference, IReferenceSetter, IHasCreatedAt
{
    public string Key { get; private set; }
    public string Domain { get; private set; }
    public string Flow { get; init; }
    public string Version { get; private set; }
    public WorkflowType Type { get; private set; }
    public WorkflowTimeout Timeout { get; private set; }
    
    public IReadOnlyCollection<LanguageLabel> Labels => labels.AsReadOnly();
    public IReadOnlyCollection<IReference> Functions => functions.AsReadOnly();
    public IReadOnlyCollection<State> States => states.AsReadOnly();
    public Transition StartTransition { get; private set; }
}
```

**Components:**
- **States**: Workflow states and their configurations
- **Transitions**: Allowed transitions between states
- **Functions**: Reusable function definitions
- **Labels**: Multi-language labels for UI
- **Extensions/Features**: Reusable extensions and feature references
- **SharedTransitions**: Cross-state transitions defined at workflow level
- **Cancel/UpdateData**: Workflow-level transitions for cancellation and data updates

**Related Definition Models:**
- **State** and **Transition** models with rule policies and validation
- **ErrorBoundary** policies (retry, timeout, backoff, fallback actions)
- **TimerConfig** and **TimerSchedule** for timer-driven execution
- **Extensions**, **Functions**, and **Views** as reusable definition artifacts
- **Schemas** for multi-schema references and identity

### 2. WorkflowTask (Abstract Base)

Base class for all task types in the system.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DaprHttpEndpointTask), typeDiscriminator: "1")]
[JsonDerivedType(typeof(DaprBindingTask), typeDiscriminator: "2")]
[JsonDerivedType(typeof(DaprServiceTask), typeDiscriminator: "3")]
[JsonDerivedType(typeof(DaprPubSubTask), typeDiscriminator: "4")]
[JsonDerivedType(typeof(HumanTask), typeDiscriminator: "5")]
[JsonDerivedType(typeof(HttpTask), typeDiscriminator: "6")]
[JsonDerivedType(typeof(ScriptTask), typeDiscriminator: "7")]
[JsonDerivedType(typeof(NotificationTask), typeDiscriminator: "10")]
[JsonDerivedType(typeof(StartTask), typeDiscriminator: "11")]
[JsonDerivedType(typeof(DirectTriggerTask), typeDiscriminator: "12")]
[JsonDerivedType(typeof(GetInstanceDataTask), typeDiscriminator: "13")]
[JsonDerivedType(typeof(SubProcessTask), typeDiscriminator: "14")]
[JsonDerivedType(typeof(GetInstancesTask), typeDiscriminator: "15")]
public abstract class WorkflowTask : IDomainEntity, ITaskReference, IReferenceSetter
{
    public string Key { get; private set; }
    public string Flow { get; init; }
    public string Domain { get; private set; }
    public string Version { get; private set; }
    public string Type { get; protected set; }
    public JsonElement Config { get; private set; }
}
```

**Task Types:**
- **HTTP/Dapr**: `HttpTask`, `DaprServiceTask`, `DaprHttpEndpointTask`, `DaprBindingTask`, `DaprPubSubTask`
- **Human/Script/Notification**: `HumanTask`, `ScriptTask`, `NotificationTask`
- **Triggers & Queries**: `StartTask`, `DirectTriggerTask`, `GetInstanceDataTask`, `GetInstancesTask`
- **Evaluation/Timer**: `ConditionTask`, `TimerTask`
- **Subflow**: `SubProcessTask`

### 3. HumanTask (Derived Task)

Specialized task requiring human interaction.

```csharp
public class HumanTask : WorkflowTask
{
    public string Title { get; private set; }
    public string Instructions { get; private set; }
    public string AssignedTo { get; private set; }
    public DateTime? DueDate { get; private set; }
    public JsonElement Form { get; private set; }
    public int ReminderIntervalMinutes { get; private set; }
    public int EscalationTimeoutMinutes { get; private set; }
    public string EscalationAssignee { get; private set; }
}
```

## Supporting Domain Modules

- **Scripting**: Mapping contracts and script context models under `Scripting/`.
- **Execution/Transitions**: Transition execution policies and helpers under `Execution/`.
- **QueryExtensions**: Filtering and query specifications for instance data.
- **Security**: Schema and input validation helpers.
- **Specifications**: Domain specifications used for filtering and evaluation.
- **Caching/Monitoring/Resilience**: Cross-cutting domain services.

## Value Objects & Supporting Models

### 1. JsonData

Encapsulates JSON data with deterministic normalization and merge support.

```csharp
public class JsonData
{
    public string Json { get; private set; }
    public string NormalizedJson { get; }
    public JsonElement JsonElement => JsonSerializer.Deserialize<JsonElement>(Json, JsonSerializerConstants.JsonOptions)!;
    public JsonData Merge(JsonData newData) { /* merge using ObjectMerger */ }
}
```

### 2. ScriptCode

Encapsulates script code with encoding support (Base64 or Native) and mapping type.

```csharp
public sealed class ScriptCode
{
    public string Location { get; private set; }
    public string Code { get; private set; }
    public MappingType Type { get; private set; }
    public CodeEncoding Encoding { get; private set; }
    public string DecodedCode { get; }
}
```

### 3. TimerSchedule

Represents schedule definitions for timers (cron, interval, and date-based schedules) used by timer tasks.

## Repository Interfaces

Domain-defined repository contracts.

```csharp
public interface IInstanceRepository : IRepository<Instance, Guid>
{
    Task<Instance?> FindByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<Instance> GetActiveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<InstanceAndDataModel>> GetActiveDataListAsync(CancellationToken cancellationToken = default);
}

public interface IInstanceTaskRepository : IRepository<InstanceTask, Guid>
{
    // Task-specific repository methods
}
```

Additional repositories include:
- `IInstanceTransitionRepository`
- `IInstanceCorrelationRepository`
- `IInstanceJobRepository`

## Domain Services

### 1. Result Rule Engine

Validates business rules using the Result Pattern without throwing exceptions.

```csharp
public interface IResultRuleEngine<T>
{
    Result Validate(T context);
    void SetRules(IEnumerable<IResultRule<T>> rules);
}

public interface IResultRule<T>
{
    bool IsApplicable(T context);
    Result Validate(T context);
}

public class ResultRuleEngine<T> : IResultRuleEngine<T> { /* fail-fast validation */ }
```

Used by policies (e.g., transition validation) to enforce domain rules without exceptions.

## Distributed Events

Instances publish a distributed event when a SubFlow changes state to keep the parent instance in sync. The event contract lives in `BBT.Workflow.Events.Contracts`.

## Constants and Configurations

### 1. Domain Constants

```csharp
public class WorkflowConstants
{
    public const string DefaultVersion = "1.0.0";
    public const int MaxKeyLength = 100;
    public const int MaxVersionLength = 20;
    public const int MaxDomainLength = 50;
}

public class InstanceConstants
{
    public const int MaxKeyLength = WorkflowConstants.MaxKeyLength;
    public const int MaxStatusLength = 20;
}
```

### 2. Status Types

Instance and task statuses are represented by strongly-typed domain constructs:

- `InstanceStatus` (Active, Busy, Completed, Faulted, Passive)
- `TaskStatus` (Waiting, Busy, Completed, Faulted)
- `BusinessStatus` (Unknown, Success, Failed)

## Validation Rules

The domain models enforce validation through `Check` helpers and domain constants. Constructors and setters validate key lengths, domain identifiers, and version formats consistently across Definitions and Instances.

## Entity Relationships

```
Instance (1) ──────── (*) InstanceData
    │
    ├── (1) ──────── (*) InstanceTransition
    │
    ├── (1) ──────── (*) InstanceTask
    │
    ├── (1) ──────── (*) InstanceCorrelation
    │
    └── (1) ──────── (*) InstanceJob

Workflow (1) ──────── (*) State
    │
    ├── (1) ──────── (*) Function
    │
    ├── (1) ──────── (*) Extension
    │
    └── (1) ──────── (*) Transition
```

This domain model design ensures strong business rule enforcement, clear entity boundaries, and proper encapsulation of workflow-related concepts. 