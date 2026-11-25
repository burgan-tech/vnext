namespace BBT.Workflow.Scripting;

/// <summary>
/// Binds a hop's raw request payload (<see cref="ScriptContext.HopInput"/>, the expression variable <c>input</c>) onto a
/// context right after it is built, for the builders that do not go through
/// <c>TransitionExecutionContext.GetOrBuildScriptContextAsync</c> (which binds it itself).
/// </summary>
internal static class ScriptContextHopInputExtensions
{
    /// <summary>Awaits the build and binds <paramref name="hopInput"/> (<c>null</c> is a valid value).</summary>
    internal static async Task<ScriptContext> WithHopInputAsync(this Task<ScriptContext> build, object? hopInput)
    {
        ArgumentNullException.ThrowIfNull(build);
        var context = await build.ConfigureAwait(false);
        context.BindHopInput(hopInput);
        return context;
    }
}
