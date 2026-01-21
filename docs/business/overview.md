# vNext Workflow Engine: Business Overview

## 1. Executive Summary
> **Note:** This section is currently a draft and will be refined to align with specific business value propositions.
>
> **Target Audience:** Technical Architects, Product Owners, and System Integrators.

The **vNext Workflow Engine** is a high-performance, .NET-based orchestration platform designed to streamline complex business processes. Built on a clean architecture, it provides a flexible foundation for automating state-based workflows with reliability and scale.

**Key Value Propositions:**
* **Flexibility:** Dynamic C# scripting and hierarchical workflow definitions allow for rapid adaptation to changing business rules.
* **Extensibility:** Event-driven architecture and Dapr integration ensure seamless connection with existing microservices.
* **Reliability:** Built-in multi-tenancy and the Inbox/Outbox pattern guarantee data integrity and isolation.

## 2. Platform Capabilities

### Workflow Definition and Management
Workflows in vNext are modeled as strongly-typed JSON definitions, which are hydrated into the `BBT.Workflow.Definitions.Workflow` aggregate. This "JSON-first" approach allows workflow logic to be stored, versioned, and transmitted easily, while the engine operates on strictly typed objects at runtime.

* **Definition Model:** The core graph—including `State`, `Transition`, and `SubFlow`—uses `[JsonConstructor]` attributes, effectively making the workflow a deserializable JSON document.
* **Builder API:** For code-first scenarios, the engine provides a fluent Builder API (`Workflow.Create()`, `State.Create()`), allowing developers to construct workflows programmatically with full compile-time safety.
* **Graph Resolution:** The `Workflow` aggregate encapsulates graph navigation, offering methods like `FindTransitionInContext` to resolve paths dynamically at runtime.

### State Machine with Conditional Transitions
The engine utilizes a directed graph state machine where every node is a `State` containing a collection of `Transitions`. Transition logic is enforced by the `StateTransitionPolicy`, ensuring that movement between states adheres to strict validation rules.

* **Trigger Types:** Transitions support multiple triggers, including `Manual` (user action), `Event` (external message), `Automatic` (logic-based), and `Scheduled` (timer-based).
* **Availability:** Shared transitions can be defined globally and made available to specific states via the `AvailableIn` property, reducing redundancy in the workflow graph.
* **Validation:** The `StateTransitionPolicy` validates execution context, ensuring that the current state matches the transition source and that the actor (User vs. System) is authorized to trigger the move.

### Multi-tenant Architecture (Multi-Schema)
Multi-tenancy is baked into the domain kernel. The execution context (`WorkflowExecutionContext`) carries an explicit `Domain` identifier, ensuring strict data isolation across tenants.

* **Schema Isolation:** The `RuntimeOptions` configuration defines a registry of system schemas (`RuntimeSysSchemaInfo`). The infrastructure layer uses this registry to dynamically map logical schema names to the underlying Entity Framework `DbContext` types.
* **Distributed Locking:** Concurrency is managed via domain-aware locking keys (`vnext:{Domain}:{WorkflowKey}:{InstanceId}`), preventing race conditions even in high-throughput, multi-tenant environments.
* **Validation:** The `SyncSchemaValidator` enforces naming conventions and security boundaries, preventing cross-tenant data leakage at the API boundary.

### Hierarchical Workflows (SubFlows)
vNext supports complex orchestration via **SubFlows**, allowing parent workflows to embed child processes. This is modeled via the `SubFlow` value object in the state definition.

* **Blocking vs. Non-Blocking:** The engine supports both blocking `SubFlow` (waits for child completion) and non-blocking `SubProcess` execution types.
* **Data Mapping:** Input and output data are shaped using dynamic C# mappings (`ScriptCode`), allowing the parent to inject data into the child and merge the results back upon completion.
* **Lifecycle Management:** The `PipelineDirectives` mechanism tracks subflow states, enabling the engine to seamlessly resume the parent workflow when a child process finishes or fails.

### Real-time and Asynchronous Execution
* **Asynchronous Pipeline:** The transition pipeline (`TransitionRunner`) is fully asynchronous, utilizing `CancellationToken` propagation for responsive cancellation.
* **Inbox/Outbox Pattern:** Reliability is provided via Aether Inbox/Outbox backed by `MessagingDbContext` (schema `sys_queues`) and configured in the HttpApi layer (`AddAetherOutbox<MessagingDbContext>()`, `AddAetherInbox<MessagingDbContext>()`).

## 3. Core Features

### Scripting Engine
The engine features a powerful **Dynamic C# Scripting Engine** powered by Roslyn (`Microsoft.CodeAnalysis`), allowing for complex business logic to be executed on the fly without recompilation.

* **Context-Aware:** Scripts run within a rich `ScriptContext` that provides access to the current `Instance`, `Workflow` definition, HTTP `Headers`, and `TaskResponse` data.
* **Compilation:** The `IScriptEngine` compiles arbitrary C# code into concrete instances, supporting dependency injection so scripts can leverage registered domain services.
* **Consistency:** The `ScriptContextFactory` ensures that all scripts—whether for conditions, mappings, or timers—receive a normalized, consistent view of the runtime data.

### Auto Transitions
Automatic transitions allow the engine to move the workflow forward without human intervention based on data conditions.

* **Evaluation:** The `IAutoConditionEvaluator` executes dynamic C# conditions defined in `Transition.Rule`. If a condition returns `Satisfied`, the engine proceeds.
* **Chaining:** The `ITransitionRunner` orchestrates chained execution. If an auto-transition is satisfied, the pipeline sets a `NextTransition` directive and the runner executes the next hop **in a new DI scope with a `RequiresNew` Unit of Work** for isolation.
* **Fallbacks:** States can define a `DefaultAutoTransition`, acting as a "catch-all" path when no other specific conditions are met.

### Service Discovery
The engine abstracts external dependencies via a robust **Service Discovery** layer (`IDomainDiscoveryResolver`), allowing workflows to interact with external systems without hardcoded endpoints.

* **Resolution:** Domains resolve dynamically to either standard HTTP URLs or Dapr App IDs.
* **Resilience:** The `DomainRegistrationService` utilizes **Polly** policies (Retry, Circuit Breaker, Timeout) to ensure robust communication with external microservices.
* **Integration:** Task invokers (e.g., `DaprServiceTaskInvoker`) rely on this discovery data to route execution requests appropriately, seamlessly handling the difference between a direct REST call and a sidecar invocation.

## 4. Integration Capabilities
* **REST API:** Exposes workflow operations for external systems via the `Orchestration API`.
* **Dapr Integration:** Deep integration with Dapr for Service Invocation, Pub/Sub messaging, and Input/Output Bindings (`DaprBindingTaskInvoker`).
* **Event-Driven:** Supports asynchronous messaging patterns, enabling the workflow to react to external domain events.

## 5. Operational Features
* **OpenTelemetry:** Provides end-to-end visibility via Logging, Tracing, and Metrics.
* **Health Monitoring:** Dedicated health endpoints for orchestration and execution hosts.