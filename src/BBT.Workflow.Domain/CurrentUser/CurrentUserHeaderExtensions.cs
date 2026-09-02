using BBT.Aether.Users;

namespace BBT.Workflow.CurrentUser;

/// <summary>
/// The default caller-role provider's role lookup.
/// <para>
/// What used to live here — header key constants, <c>ChangeFromHeaders</c>, <c>ToForwardHeaders</c>
/// and <c>ParseRolesFromHeader</c> — now ships in the SDK as <c>BBT.Aether.Users.AetherClaimTypes</c>
/// and <c>BBT.Aether.Users.CurrentUserHeaderExtensions</c>. Keeping a second copy meant two
/// definitions of one header contract, free to drift; the SDK's version is also what
/// <c>HeaderCurrentUserResolver</c> reads on the way in, so a user captured by the framework and one
/// restored by this runtime are now guaranteed to agree. The SDK's <c>ToForwardHeaders</c> emits
/// <c>position</c> from <c>ICurrentUser.Position</c>, which is what the local copy needed an extra
/// parameter for.
/// </para>
/// <para>
/// Deliberately NOT named <c>CurrentUserHeaderExtensions</c> any more: the SDK owns that name, and two
/// same-named static classes in imported namespaces make every unqualified member access ambiguous.
/// </para>
/// </summary>
public static class CallerRoleHeaderExtensions
{
    /// <summary>
    /// Resolves the caller role list for the default provider. Prefers all roles on the current user;
    /// when the user carries none, falls back to the roles parsed from the request <c>role</c> header;
    /// returns <c>null</c> when neither is present.
    /// <para>
    /// The header fallback is not redundant with the framework's own resolver. That one reads an
    /// ambient <c>HttpContext</c>, and several vNext paths run without one — background transition
    /// jobs, resumed pipelines — carrying the caller's headers as a plain dictionary instead.
    /// </para>
    /// <para>
    /// This is the <b>default caller-role provider's</b> implementation, reached through
    /// <c>ICallerRoleResolver</c>. Do not call it directly from an authorization surface: under a
    /// non-default provider it returns the wrong role set, and the surface silently disagrees with
    /// every other one.
    /// </para>
    /// </summary>
    /// <param name="currentUser">The current user.</param>
    /// <param name="headers">Request headers to read the <c>role</c> value from when the user has none.</param>
    public static string[]? ResolveCallerRoles(
        this ICurrentUser currentUser,
        IReadOnlyDictionary<string, string?>? headers)
    {
        if (currentUser.Roles is { Length: > 0 } roles)
            return roles;

        var headerRole = headers is not null && headers.TryGetValue(AetherClaimTypes.Role, out var value)
            ? value
            : null;

        return CurrentUserHeaderExtensions.ParseRolesFromHeader(headerRole);
    }
}
