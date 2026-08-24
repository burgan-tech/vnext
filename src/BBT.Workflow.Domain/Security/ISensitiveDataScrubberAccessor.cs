namespace BBT.Workflow.Security;

/// <summary>
/// Carries the <see cref="SensitiveDataScrubber"/> for the work in flight, so cross-cutting
/// concerns that are far from the instance — a logger decorator, a diagnostic message builder —
/// can scrub without being handed the instance data themselves.
/// <para>
/// Registered <b>scoped</b> and deliberately mutable. It is populated when a script context is
/// built (the point where both the workflow's master schema and the instance data are in hand)
/// and read later, synchronously, from places that cannot await a schema load. An
/// <c>AsyncLocal</c> would look tempting and would not work: assigning one inside the nested
/// async call that resolves the data does not propagate back out to the caller.
/// </para>
/// <para>
/// Parallel task branches each get their own DI scope and build their own script context, so each
/// branch populates its own accessor. Anything running in a scope that never built a context
/// simply sees <see cref="SensitiveDataScrubber.None"/> — unscrubbed, which is why the scrubber
/// is a defence-in-depth layer and not the primary control.
/// </para>
/// </summary>
public interface ISensitiveDataScrubberAccessor
{
    /// <summary>
    /// The scrubber for the current scope. Never null; defaults to
    /// <see cref="SensitiveDataScrubber.None"/> before anything populates it.
    /// </summary>
    SensitiveDataScrubber Current { get; }

    /// <summary>
    /// Publishes the scrubber for the current scope, replacing any previous value.
    /// </summary>
    /// <param name="scrubber">The scrubber to publish; null resets to <see cref="SensitiveDataScrubber.None"/>.</param>
    void Set(SensitiveDataScrubber? scrubber);
}

/// <summary>
/// Default scoped implementation of <see cref="ISensitiveDataScrubberAccessor"/>.
/// </summary>
public sealed class SensitiveDataScrubberAccessor : ISensitiveDataScrubberAccessor
{
    /// <inheritdoc />
    public SensitiveDataScrubber Current { get; private set; } = SensitiveDataScrubber.None;

    /// <inheritdoc />
    public void Set(SensitiveDataScrubber? scrubber) => Current = scrubber ?? SensitiveDataScrubber.None;
}
