using System.Collections.Immutable;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Describes how the transition pipeline should execute for a given trigger or scenario:
/// which lifecycle steps are skipped, and whether auto-chaining and subflow handling are permitted.
/// </summary>
public sealed class PipelineExecutionProfile
{
    /// <summary>
    /// Gets a display name for this profile (for logging and telemetry).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets lifecycle step orders that are excluded from execution for this profile.
    /// </summary>
    public required IReadOnlySet<int> ExcludedStepOrders { get; init; }

    /// <summary>
    /// Gets a value indicating whether automatic transition chaining may continue after this run.
    /// </summary>
    public required bool AllowAutoChain { get; init; }

    /// <summary>
    /// Gets a value indicating whether subflow-related pipeline steps should run where applicable.
    /// </summary>
    public required bool AllowSubFlow { get; init; }

    // NOTE: ResourceLock is intentionally NOT excluded here. It is per-transition business logic
    // (acquire/release a shared-resource lock keyed by the transition's script), not chain-head
    // request setup like SetBusy. Excluding it made a schema-valid `resourceLock` on an auto-chained
    // transition silently no-op — and it is inconsistent with the Scheduled/Event profiles, which
    // already run it. Only the genuine per-request/subflow-prelude steps stay excluded.
    private static readonly ImmutableHashSet<int> AutoChainExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.ForwardToActiveSubflow,
        LifecycleOrder.SetBusy,
        LifecycleOrder.ApplyTimeoutState);

    private static readonly ImmutableHashSet<int> ScheduledExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.ForwardToActiveSubflow);

    private static readonly ImmutableHashSet<int> EventExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.ForwardToActiveSubflow);

    private static readonly ImmutableHashSet<int> ErrorBoundaryExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.ForwardToActiveSubflow,
        LifecycleOrder.ResourceLock);

    private static readonly PipelineExecutionProfile ManualInstance = new()
    {
        Name = "Manual",
        ExcludedStepOrders = ImmutableHashSet<int>.Empty,
        AllowAutoChain = true,
        AllowSubFlow = true,
    };

    private static readonly PipelineExecutionProfile AutoChainInstance = new()
    {
        Name = "AutoChain",
        ExcludedStepOrders = AutoChainExcluded,
        AllowAutoChain = true,
        AllowSubFlow = false,
    };

    private static readonly PipelineExecutionProfile ScheduledInstance = new()
    {
        Name = "Scheduled",
        ExcludedStepOrders = ScheduledExcluded,
        AllowAutoChain = true,
        AllowSubFlow = false,
    };

    private static readonly PipelineExecutionProfile EventInstance = new()
    {
        Name = "Event",
        ExcludedStepOrders = EventExcluded,
        AllowAutoChain = true,
        AllowSubFlow = true,
    };

    private static readonly PipelineExecutionProfile ErrorBoundaryInstance = new()
    {
        Name = "ErrorBoundary",
        ExcludedStepOrders = ErrorBoundaryExcluded,
        AllowAutoChain = true,
        AllowSubFlow = false,
    };

    /// <summary>
    /// Creates the profile used for manual transitions: full pipeline, no exclusions; auto-chain and subflow enabled.
    /// </summary>
    public static PipelineExecutionProfile ForManual() => ManualInstance;

    /// <summary>
    /// Creates the profile optimized for automatic (auto-chain) transitions:
    /// excludes preflight, parent/subflow forwarding, busy marking, and timeout application; subflow disabled.
    /// Resource lock steps still run so a <c>resourceLock</c> defined on an auto-chained transition is honored.
    /// </summary>
    public static PipelineExecutionProfile ForAutoChain() => AutoChainInstance;

    /// <summary>
    /// Creates the profile for scheduled transitions: excludes preflight and subflow forwarding steps; subflow disabled.
    /// </summary>
    public static PipelineExecutionProfile ForScheduled() => ScheduledInstance;

    /// <summary>
    /// Creates the profile for event-triggered transitions: excludes preflight and forward-to-active-subflow; subflow enabled.
    /// </summary>
    public static PipelineExecutionProfile ForEvent() => EventInstance;

    /// <summary>
    /// Creates the minimal profile for error-boundary transitions: excludes lock, scheduling, auto, and subflow prelude steps;
    /// auto-chain and subflow are disabled.
    /// </summary>
    public static PipelineExecutionProfile ForErrorBoundary() => ErrorBoundaryInstance;
}
