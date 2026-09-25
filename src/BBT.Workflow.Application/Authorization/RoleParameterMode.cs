namespace BBT.Workflow.Authorization;

/// <summary>
/// How the <c>role</c> request parameter of <c>authorize</c> composes with the caller's role set.
/// Stated by each <see cref="ICallerRoleResolver"/>, never inferred from a provider name at a surface.
/// </summary>
public enum RoleParameterMode
{
    /// <summary>
    /// The parameter stands in only when the provider resolved no roles; the <c>ack</c> target adds it
    /// on every path. The default provider's mode: its own source is already the caller's <c>role</c>
    /// header, so the parameter is the same claim through a different door.
    /// </summary>
    Fallback = 0,

    /// <summary>
    /// The parameter behaves exactly like a request <c>role</c> header: when the request carries no
    /// header of its own, the parameter is handed to the resolver as that header, and the resolver's
    /// header precedence applies. A real header wins. morph-idm's mode since 2026-09-25, where a
    /// header makes the role set and the identity service is not asked.
    /// </summary>
    AsRoleHeader = 1
}
