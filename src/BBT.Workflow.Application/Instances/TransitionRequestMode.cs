using BBT.Workflow.Execution;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Instances;

/// <summary>
/// The one place a transition REQUEST's sync/async mode is decided (vnext#1003). Shared by
/// <see cref="InstanceCommandAppService"/>, which runs the request on the instance itself, and the
/// SubFlow proxy, which decides the mode on the parent and has the active child run in exactly it —
/// two copies of this rule would let the client's 200/202 disagree with what actually ran.
/// </summary>
internal static class TransitionRequestMode
{
    /// <summary>
    /// Resolves the caller's mode (the <c>sync</c> flag) and the effective mode: a transition's
    /// <c>executionType</c> (inner) wins over the flow's (outer), and either overrides the caller's
    /// flag; absent any definition the caller's mode stands (pre-#1003 behaviour).
    /// <para>
    /// EXCEPT runtime-internal calls (<see cref="TransitionInput.SuppressResponseEnrichment"/>): the
    /// subflow forward and the proxy already decided the mode one level up and send it as the sync
    /// flag, so the receiving level must NOT re-resolve it from its own definition.
    /// </para>
    /// Records requested vs effective on the current span when the definition is consulted.
    /// </summary>
    public static (ExecMode Caller, ExecMode Effective) Resolve(
        Definitions.Workflow workflow,
        string transitionKey,
        TransitionInput input)
    {
        var callerMode = input.Sync ? ExecMode.Sync : ExecMode.Async;
        if (input.SuppressResponseEnrichment)
            return (callerMode, callerMode);

        // Alias-aware: a well-known transition (cancel/updateData/exit) invoked by its reserved alias
        // while the workflow uses a custom key resolves here too, matching the pipeline's own lookup.
        var transition = workflow.ResolveWellKnownTransition(transitionKey)
                         ?? workflow.FindTransitionInContext(transitionKey);
        var effectiveMode = ExecutionModeResolver.Resolve(
            transition?.ExecutionType, workflow.ExecutionType, callerMode);
        Tag(callerMode, effectiveMode, transition?.ExecutionType, workflow.ExecutionType);
        return (callerMode, effectiveMode);
    }

    /// <summary>
    /// Records the requested vs effective execution mode on the current trace span (vnext#1003), so an
    /// operator can see when a flow/transition <c>executionType</c> definition overrode the caller's
    /// <c>sync</c> query parameter. Uses the existing telemetry span; no new persistence.
    /// </summary>
    public static void Tag(
        ExecMode requested,
        ExecMode effective,
        Definitions.ExecutionType? transitionExecutionType,
        Definitions.ExecutionType? flowExecutionType)
    {
        var activity = System.Diagnostics.Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag(TelemetryConstants.TagNames.ExecutionRequested, ToModeTag(requested));
        activity.SetTag(TelemetryConstants.TagNames.ExecutionEffective, ToModeTag(effective));
        if (ExecutionModeResolver.IsOverriddenByDefinition(transitionExecutionType, flowExecutionType, requested))
        {
            activity.SetTag(TelemetryConstants.TagNames.ExecutionOverridden, true);
        }
    }

    private static string ToModeTag(ExecMode mode) => mode == ExecMode.Async ? "ASYNC" : "SYNC";
}
