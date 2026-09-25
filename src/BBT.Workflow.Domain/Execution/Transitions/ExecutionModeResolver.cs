using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution;

/// <summary>
/// Resolves the EFFECTIVE execution mode for a transition (vnext#1003) from the definitions and the
/// caller's requested mode. Precedence, highest first:
/// <list type="number">
/// <item>the transition's own <c>executionType</c> (the inner definition),</item>
/// <item>the flow's <c>executionType</c> (the outer definition),</item>
/// <item>the caller's requested mode (the <c>sync</c> query parameter).</item>
/// </list>
/// When any definition is present it is the source of truth and the caller's requested mode is ignored;
/// when none is present the caller's mode stands, preserving the pre-#1003 behaviour. Automatic
/// transitions do not go through this resolver — they always run inline as part of the chain.
/// </summary>
public static class ExecutionModeResolver
{
    /// <summary>
    /// Returns the effective mode. <paramref name="callerMode"/> is what the caller asked for (derived
    /// from the <c>sync</c> query parameter). A defined <see cref="ExecutionType"/> — transition first,
    /// then flow — overrides it.
    /// </summary>
    public static ExecMode Resolve(
        ExecutionType? transitionExecutionType,
        ExecutionType? flowExecutionType,
        ExecMode callerMode)
    {
        var defined = transitionExecutionType ?? flowExecutionType;
        if (defined is null)
        {
            return callerMode;
        }

        return defined.IsAsync ? ExecMode.Async : ExecMode.Sync;
    }

    /// <summary>
    /// True when a definition (transition or flow) is present and it dictates a mode different from what
    /// the caller requested — i.e. the caller's <c>sync</c> query parameter was overridden. This is the
    /// signal surfaced on the trace (requested vs effective) so an operator can see the override.
    /// </summary>
    public static bool IsOverriddenByDefinition(
        ExecutionType? transitionExecutionType,
        ExecutionType? flowExecutionType,
        ExecMode callerMode)
    {
        var defined = transitionExecutionType ?? flowExecutionType;
        return defined is not null && Resolve(transitionExecutionType, flowExecutionType, callerMode) != callerMode;
    }
}
