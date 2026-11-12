namespace BBT.Workflow.Scripting.Caller;

/// <summary>
/// What a caller-role provider answered: the roles, whether the answer is a failure, and whether the set
/// was taken from the request's <c>role</c> header.
/// </summary>
/// <param name="Roles">The roles; empty when the caller holds none or when <paramref name="Failed"/>.</param>
/// <param name="Failed">True when the provider could not answer; the roles must not be used.</param>
/// <param name="FromHeader">True when the set came from the request's <c>role</c> header.</param>
public sealed record CallerRoleSnapshot(IReadOnlyList<string> Roles, bool Failed, bool FromHeader);

/// <summary>
/// The request-bound <see cref="ICallerAccessor"/>: resolves the caller's roles on the first read through a
/// delegate supplied by the application layer, at most once, and shares that answer with every later read.
/// </summary>
/// <remarks>
/// A failed answer is sticky: every read throws <see cref="CallerRolesUnavailableException"/> and
/// <see cref="ResolutionFailed"/> stays true, which is what the selection loops check after each rule so a
/// rule that swallows the exception cannot turn an unresolved caller into a match or a silent fallback.
/// <see cref="Close"/> ends the exposure once the selection is over, so later scripts sharing the same
/// context (a function's tasks) cannot read roles the response cache does not vary on.
/// </remarks>
public sealed class CallerAccessor : ICallerAccessor
{
    private readonly Lazy<Task<CallerRoleSnapshot>> _resolution;
    private volatile bool _closed;
    private volatile bool _failed;
    private CallerRoleSnapshot? _resolved;

    /// <summary>Creates an accessor that resolves through <paramref name="resolve"/> on the first read.</summary>
    public CallerAccessor(Func<Task<CallerRoleSnapshot>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolution = new Lazy<Task<CallerRoleSnapshot>>(resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public bool IsAvailable => !_closed;

    /// <inheritdoc />
    public bool ResolutionFailed => _failed;

    /// <inheritdoc />
    public bool? RolesFromHeader => _resolved?.FromHeader;

    /// <summary>
    /// True once a read started the resolution. Lets tests and diagnostics prove that a selection whose
    /// rules never read roles made no provider call.
    /// </summary>
    public bool ResolutionStarted => _resolution.IsValueCreated;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RolesAsync()
    {
        if (_closed)
            throw new CallerRolesUnavailableException(
                "Caller roles are no longer available: they are exposed to the rule-based selection only.");

        CallerRoleSnapshot snapshot;
        try
        {
            snapshot = await _resolution.Value.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failed = true;
            throw new CallerRolesUnavailableException("Caller roles could not be resolved.", ex);
        }

        if (snapshot.Failed)
        {
            _failed = true;
            throw new CallerRolesUnavailableException("Caller roles could not be resolved.");
        }

        _resolved = snapshot;
        return snapshot.Roles;
    }

    /// <inheritdoc />
    public async Task<bool> HasRoleAsync(string role)
    {
        var roles = await RolesAsync().ConfigureAwait(false);
        return roles.Contains(role, StringComparer.Ordinal);
    }

    /// <summary>Ends the exposure: later reads throw and <see cref="IsAvailable"/> turns false.</summary>
    public void Close() => _closed = true;
}
