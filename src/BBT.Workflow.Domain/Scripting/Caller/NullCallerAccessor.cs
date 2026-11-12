namespace BBT.Workflow.Scripting.Caller;

/// <summary>
/// The accessor of a surface with no caller (pipeline tasks, automatic rules, timers, event handlers,
/// the Execution host). Reports <see cref="ICallerAccessor.IsAvailable"/> false and throws on a read —
/// never an empty role set, which a rule could not tell apart from a caller who holds no role.
/// </summary>
public sealed class NullCallerAccessor : ICallerAccessor
{
    /// <summary>The shared instance.</summary>
    public static NullCallerAccessor Instance { get; } = new();

    private NullCallerAccessor()
    {
    }

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public bool ResolutionFailed => false;

    /// <inheritdoc />
    public bool? RolesFromHeader => null;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> RolesAsync() => throw Unavailable();

    /// <inheritdoc />
    public Task<bool> HasRoleAsync(string role) => throw Unavailable();

    private static CallerRolesUnavailableException Unavailable() => new(
        "Caller roles are not available here: they are exposed to rule-based schema and view selection on a caller's request only.");
}
