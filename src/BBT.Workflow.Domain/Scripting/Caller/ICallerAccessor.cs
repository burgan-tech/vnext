namespace BBT.Workflow.Scripting.Caller;

/// <summary>
/// The caller's role set as the runtime resolved it, for rule scripts that select a contract or a view
/// (transition <c>schemas</c>, function <c>inputSchema</c>/<c>outputSchema</c>/<c>inputView</c>/<c>outputView</c>,
/// state/transition <c>views</c>). Reached through <see cref="ScriptContext.Caller"/>.
/// </summary>
/// <remarks>
/// <para>
/// The set comes from the configured caller-role provider (<c>default</c>: token claims, else the
/// gateway-supplied <c>role</c> header; <c>morph-idm</c>: the identity service). It is the same set the
/// runtime uses for its own role decisions and for caller-scoped cache keys, so a rule never disagrees with
/// them. Prefer it over reading <c>Headers["role"]</c>.
/// </para>
/// <para>
/// Nothing is resolved until a rule asks; a rule that never reads roles costs no provider call. Within one
/// request the provider answers at most once, however many rules ask.
/// </para>
/// <para>
/// Selecting by role is a <b>selection</b> (which form, which payload contract to offer), not an
/// authorization boundary; access decisions stay with the gateway and <c>queryRoles</c>.
/// </para>
/// </remarks>
public interface ICallerAccessor
{
    /// <summary>
    /// True when this surface has a caller whose roles can be asked for. False on surfaces with no live
    /// request (pipeline tasks, automatic rules, timers, event handlers) and after the selection that
    /// exposed it has finished.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True once a role read failed because the provider could not answer (for example the identity
    /// service timed out). The runtime then fails the whole selection with a retryable error instead of
    /// falling through to a later entry; a rule cannot opt out of this by catching the exception.
    /// </summary>
    bool ResolutionFailed { get; }

    /// <summary>
    /// After a successful read: true when the set came from the request's <c>role</c> header rather than
    /// from the identity provider. That header is asserted upstream (the gateway), not verified here.
    /// Null until roles were read. Diagnostic only; do not branch on it.
    /// </summary>
    bool? RolesFromHeader { get; }

    /// <summary>The caller's roles (empty when the caller holds none).</summary>
    /// <exception cref="CallerRolesUnavailableException">
    /// The surface has no caller (<see cref="IsAvailable"/> is false) or the provider could not answer.
    /// </exception>
    Task<IReadOnlyList<string>> RolesAsync();

    /// <summary>True when the caller holds <paramref name="role"/> (ordinal comparison).</summary>
    /// <exception cref="CallerRolesUnavailableException">See <see cref="RolesAsync"/>.</exception>
    Task<bool> HasRoleAsync(string role);
}
