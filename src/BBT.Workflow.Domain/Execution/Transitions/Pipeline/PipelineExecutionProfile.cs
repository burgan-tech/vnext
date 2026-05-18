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

    private static readonly ImmutableHashSet<int> AutoChainExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.CheckParentUpdateDataTransition,
        LifecycleOrder.ForwardToActiveSubflow,
        LifecycleOrder.SetBusy,
        LifecycleOrder.ResourceLock,
        LifecycleOrder.ApplyTimeoutState);

    private static readonly ImmutableHashSet<int> ScheduledExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.CheckParentUpdateDataTransition,
        LifecycleOrder.ForwardToActiveSubflow);

    private static readonly ImmutableHashSet<int> EventExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.ForwardToActiveSubflow);

    private static readonly ImmutableHashSet<int> ErrorBoundaryExcluded = ImmutableHashSet.Create(
        LifecycleOrder.Preflight,
        LifecycleOrder.CheckParentUpdateDataTransition,
        LifecycleOrder.ForwardToActiveSubflow,
        LifecycleOrder.ResourceLock,
        LifecycleOrder.Auto,
        LifecycleOrder.Schedule);

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
        AllowAutoChain = false,
        AllowSubFlow = false,
    };

    /// <summary>
    /// Creates the profile used for manual transitions: full pipeline, no exclusions; auto-chain and subflow enabled.
    /// </summary>
    public static PipelineExecutionProfile ForManual() => ManualInstance;

    /// <summary>
    /// Creates the profile optimized for automatic (auto-chain) transitions:
    /// excludes preflight, parent/subflow forwarding, resource lock, and timeout application; subflow disabled.
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
