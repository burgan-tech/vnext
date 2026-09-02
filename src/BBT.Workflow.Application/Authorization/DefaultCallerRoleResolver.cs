using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The default <see cref="ICallerRoleResolver"/>: the runtime's original behaviour, unchanged.
/// Roles come from <c>ICurrentUser.Roles</c>, falling back to the request's <c>role</c> header.
/// <para>
/// Purely in-process — it never fails, so every call site's failure branch is dead code under this
/// provider. That is intentional: the branch exists for providers that do I/O, and keeping it on the
/// default path means switching providers changes configuration only, never control flow.
/// </para>
/// </summary>
public sealed class DefaultCallerRoleResolver(ICurrentUser currentUser) : ICallerRoleResolver
{
    /// <inheritdoc />
    public Task<Result<string[]?>> ResolveRolesAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<string[]?>.Ok(currentUser.ResolveCallerRoles(headers)));
}
