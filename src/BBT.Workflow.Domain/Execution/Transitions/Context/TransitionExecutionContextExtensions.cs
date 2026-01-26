namespace BBT.Workflow.Execution;

/// <summary>
/// Extension methods for TransitionExecutionContext to enhance workflow operations.
/// </summary>
public static class TransitionExecutionContextExtensions
{
    /// <summary>
    /// Determines whether the current transition is a cancel transition.
    /// A transition is considered a cancel transition if it matches the workflow's configured cancel transition key.
    /// </summary>
    /// <param name="ctx">The transition execution context</param>
    /// <returns>True if this is a cancel transition, false otherwise</returns>
    public static bool IsCancelTransition(this TransitionExecutionContext ctx)
    {
        return ctx.Workflow.Cancel?.Key.Equals(ctx.Transition?.Key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition is an updateData transition.
    /// A transition is considered an updateData transition if it matches the workflow's configured updateData transition key.
    /// </summary>
    /// <param name="ctx">The transition execution context</param>
    /// <returns>True if this is an updateData transition, false otherwise</returns>
    public static bool IsUpdateDataTransition(this TransitionExecutionContext ctx)
    {
        return ctx.Workflow.UpdateData?.Key.Equals(ctx.Transition?.Key, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Determines whether the current transition is an exit transition.
    /// A transition is considered an exit transition if it matches the workflow's configured exit transition key.
    /// </summary>
    /// <param name="ctx">The transition execution context</param>
    /// <returns>True if this is an exit transition, false otherwise</returns>
    public static bool IsExitTransition(this TransitionExecutionContext ctx)
    {
        return ctx.Workflow.Exit?.Key.Equals(ctx.Transition?.Key, StringComparison.OrdinalIgnoreCase) == true;
    }
}

