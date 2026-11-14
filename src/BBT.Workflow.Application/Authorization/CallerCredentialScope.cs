using BBT.Aether.Users;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The in-process exit of an instance call: the equivalent of what the target domain's request pipeline does for the
/// same headers on a cross-domain call. Blank credentials never reach the target, and the call runs as the user its
/// header set describes — never as the ambient pipeline caller — so authorization and CreatedBy / CreatedByBehalfOf
/// are the same whether the target lives in this domain or another. See
/// <c>HttpTaskInvocation.ResolveInstanceCallCredential</c>.
/// </summary>
public static class CallerCredentialScope
{
    /// <summary>
    /// Removes the blank credentials from <paramref name="headers"/> in place, then makes that header set the current
    /// user until the returned scope is disposed. An empty set is an unauthenticated, role-less user
    /// (<c>ChangeFromHeaders</c> is a no-op for an empty set, so the ambient user would otherwise stay).
    /// </summary>
    public static IDisposable Enter(ICurrentUser currentUser, Dictionary<string, string?> headers)
    {
        HttpTaskInvocation.RemoveBlankCredentials(headers);
        return headers.Count == 0
            ? currentUser.Change(new BasicUserInfo(null))
            : currentUser.ChangeFromHeaders(headers);
    }
}
