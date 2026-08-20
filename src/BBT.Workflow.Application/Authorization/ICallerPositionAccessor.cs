namespace BBT.Workflow.Authorization;

/// <summary>
/// Supplies the caller's <c>position</c> — the organizational posting that, together with the actor
/// (<c>act_sub</c>) and subject (<c>sub</c>) identities, determines the caller's role set at an external
/// identity provider.
/// <para>
/// This exists as its own seam for two reasons. First, <c>position</c> is not yet on
/// <c>ICurrentUser</c>; when it lands there, only the HTTP implementation changes. Second, it must not
/// be read from the <c>headers</c> argument threaded through the authorization surfaces — several of
/// those call sites pass <c>null</c> headers, and a memoizing provider would freeze whichever value the
/// first caller happened to supply.
/// </para>
/// </summary>
public interface ICallerPositionAccessor
{
    /// <summary>
    /// The caller's position, or null when the request carries none.
    /// </summary>
    string? GetPosition();
}

/// <summary>
/// No-op <see cref="ICallerPositionAccessor"/> for scopes with no ambient HTTP request — workers, the
/// migrator, and tests. Registered as the fallback so those hosts construct; the HTTP hosts override it.
/// </summary>
public sealed class NullCallerPositionAccessor : ICallerPositionAccessor
{
    /// <inheritdoc />
    public string? GetPosition() => null;
}
