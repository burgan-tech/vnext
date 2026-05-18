namespace BBT.Workflow.Execution;

/// <summary>
/// Extension methods for TransitionExecutionContext to enhance workflow operations.
/// </summary>
public static class TransitionExecutionContextExtensions
{
    /// <summary>
    /// Determines whether the current transition is a cancel transition.
    /// Matches both the workflow's configured cancel key and the well-known reserved key.
    /// </summary>
    public static bool IsCancelTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return key.Equals(Definitions.WellKnownTransitionKeys.Cancel, StringComparison.OrdinalIgnoreCase)
            || ctx.Workflow.Cancel?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition is an updateData transition.
    /// Matches both the workflow's configured updateData key and the well-known reserved key.
    /// </summary>
    public static bool IsUpdateDataTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return key.Equals(Definitions.WellKnownTransitionKeys.UpdateData, StringComparison.OrdinalIgnoreCase)
            || ctx.Workflow.UpdateData?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition is an exit transition.
    /// Matches both the workflow's configured exit key and the well-known reserved key.
    /// </summary>
    public static bool IsExitTransition(this TransitionExecutionContext ctx)
    {
        var key = ctx.Transition?.Key;
        if (key is null) return false;

        return key.Equals(Definitions.WellKnownTransitionKeys.Exit, StringComparison.OrdinalIgnoreCase)
            || ctx.Workflow.Exit?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true;
    }
}

