namespace BBT.Workflow.Scripting;

/// <summary>
/// Ambient holder for the original raw request body during background (non-HTTP) execution —
/// e.g. an async transition or instance-start job, where there is no live HTTP request.
/// A job handler sets it for the duration of its <c>HandleAsync</c> scope so that every
/// <see cref="ScriptContext"/> built inside the job can resolve the raw body for signature verification.
/// </summary>
public static class RawBodyExecutionScope
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    /// <summary>The raw body for the current async scope, or <c>null</c> when not set.</summary>
    public static string? Current => CurrentValue.Value;

    /// <summary>
    /// Sets the raw body for the current async scope. Dispose the returned token to restore the previous value.
    /// </summary>
    /// <param name="rawBody">The raw request body string to expose, or null.</param>
    public static IDisposable Set(string? rawBody)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = rawBody;
        return new ScopeToken(previous);
    }

    private sealed class ScopeToken(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}
