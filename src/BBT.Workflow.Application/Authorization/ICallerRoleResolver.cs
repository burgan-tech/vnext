using BBT.Aether.Results;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Resolves the caller's role set — the "işlem seti" every authorization surface evaluates grants
/// against. This is the single seam behind which the role source is configurable: the default provider
/// reads <c>ICurrentUser.Roles</c> with the legacy <c>role</c> header as fallback, while an external
/// provider (morph-idm) fetches the set over HTTP.
/// <para>
/// Never read <c>ICurrentUser.Roles</c> or call <c>ResolveCallerRoles</c> directly at a decision point;
/// go through this service. Those static extensions remain the default provider's implementation, not
/// an alternative entry point — a surface that bypasses the resolver silently sees the wrong role set
/// under a non-default provider.
/// </para>
/// <para>
/// Implementations that perform I/O must memoize for the lifetime of the DI scope: one request means
/// at most one provider call, no matter how many surfaces ask. Failures are memoized too, so a scope
/// that could not establish the caller's roles stays failed rather than retrying per surface.
/// </para>
/// </summary>
public interface ICallerRoleResolver
{
    /// <summary>
    /// Resolves the caller's full role set. A successful result carries <c>null</c> or an empty array
    /// when the caller holds no roles — both mean the same thing to the grant evaluator, which still
    /// evaluates predefined and dynamic grants once for a role-less caller.
    /// </summary>
    /// <param name="headers">
    /// Request headers, used by the default provider for the legacy <c>role</c> fallback and by remote
    /// providers as a last-resort source of caller identity in non-HTTP scopes. May be null.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The caller's roles, or a failure when the provider could not answer. Callers MUST propagate the
    /// failure: an unresolvable role set is a denial, never an empty set.
    /// </returns>
    Task<Result<string[]?>> ResolveRolesAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a caller-supplied <c>role</c> request parameter may stand in for this provider's answer
    /// when the provider reports no roles.
    /// </summary>
    /// <remarks>
    /// <para><b>True only when the provider's own source is already the caller's own assertion.</b> The
    /// default provider reads <c>ICurrentUser.Roles</c> with the <c>role</c> header behind it, so the
    /// parameter is the same claim arriving through a different door and adds no authority.</para>
    /// <para><b>False for any provider that is an authority.</b> Under morph-idm the resolved set is
    /// the caller's operation set as the identity service reports it, and that service answering
    /// "none" (<c>204</c>) is a decision, not a gap to fill. Letting the parameter through there would
    /// let a caller name its own role — measured on the bench before this existed:
    /// <c>?queryRoles=true</c> answered <c>403</c> while <c>?queryRoles=true&amp;role=chain.admin</c>
    /// answered <c>200</c> for the same caller and instance. It is the same hole the "never forward
    /// the <c>role</c> header" rule closes, reached through the query string instead, and it matters
    /// more now that <c>authorize</c> is the only place these questions are answered: a gateway that
    /// passes the client's query string through would be admitting on the client's own claim.</para>
    /// <para>The flag lives on the resolver rather than being read from configuration so the
    /// Application layer asks the abstraction it already depends on, and so a new provider has to
    /// state its own answer instead of inheriting one.</para>
    /// </remarks>
    bool AllowsRoleParameterFallback => true;

    /// <summary>
    /// The single caller role used where a surface routes on one role (state aliasing, cache scoping).
    /// Always the first of the resolved set, so it can never disagree with it.
    /// </summary>
    public static string? SingleRoleOf(string[]? roles) =>
        roles is { Length: > 0 } ? roles[0] : null;
}
