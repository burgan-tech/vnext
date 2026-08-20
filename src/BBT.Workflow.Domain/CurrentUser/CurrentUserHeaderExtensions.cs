using BBT.Aether.Users;

namespace BBT.Workflow.CurrentUser;

/// <summary>
/// Header key constants aligned with Aether claim types used by HeaderCurrentUserResolver.
/// Used when building ICurrentUser from a request headers dictionary (e.g. in background job execution scope).
/// Also used when forwarding current user to remote or subflow requests.
/// </summary>
public static class CurrentUserHeaderKeys
{
    public const string UserId = "userId";
    public const string UserName = "sub";
    public const string Name = "given_name";
    public const string SurName = "family_name";
    public const string Role = "role";
    public const string ActorSub = "act_sub";
    public const string Position = "position";
    public const string ActorUserId = "act_uid";
    public const string ConsentId = "consent_id";
}

/// <summary>
/// Extensions for setting ICurrentUser from a headers dictionary within a scope (e.g. workflow execution).
/// Enables correct user context when execution runs outside HTTP (e.g. sync=false background jobs).
/// </summary>
public static class CurrentUserHeaderExtensions
{
    /// <summary>
    /// Changes the current user from the given headers for the lifetime of the returned disposable.
    /// When disposed, the previous user is restored. If headers are null or empty, returns a no-op disposable.
    /// </summary>
    /// <param name="currentUser">The current user service.</param>
    /// <param name="headers">Request headers (e.g. from WorkflowExecutionContext.Headers or job payload).</param>
    /// <returns>An IDisposable that restores the previous user when disposed; or a no-op if no headers.</returns>
    public static IDisposable ChangeFromHeaders(
        this ICurrentUser currentUser,
        IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null || headers.Count == 0)
            return EmptyDisposable.Instance;

        var userId = GetHeader(headers, CurrentUserHeaderKeys.UserId);
        var userName = GetHeader(headers, CurrentUserHeaderKeys.UserName);
        var name = GetHeader(headers, CurrentUserHeaderKeys.Name);
        var surname = GetHeader(headers, CurrentUserHeaderKeys.SurName);
        var rolesHeader = GetHeader(headers, CurrentUserHeaderKeys.Role);
        var roles = string.IsNullOrEmpty(rolesHeader)
            ? null
            : ParseRolesFromHeader(rolesHeader);
        var actorUserId = GetHeader(headers, CurrentUserHeaderKeys.ActorUserId);
        var actorUserName = GetHeader(headers, CurrentUserHeaderKeys.ActorSub);
        var consentId = GetHeader(headers, CurrentUserHeaderKeys.ConsentId);

        return currentUser.Change(
            userId,
            userName,
            name,
            surname,
            roles,
            actorUserId,
            actorUserName,
            consentId);
    }

    /// <summary>
    /// Builds the forward headers dictionary from the current user for remote/subflow requests.
    /// Downstream can resolve ICurrentUser from these headers.
    /// </summary>
    /// <param name="currentUser">The current user.</param>
    /// <param name="position">
    /// The caller's <c>position</c>, forwarded so a downstream domain running an external caller-role
    /// provider can resolve the same operation set. Passed explicitly because <c>position</c> is not
    /// yet carried on <see cref="ICurrentUser"/>; when it lands there this parameter's default becomes
    /// <c>currentUser.Position</c> and callers need not supply it. Omitted from the dictionary when empty.
    /// </param>
    public static Dictionary<string, string?> ToForwardHeaders(
        this ICurrentUser currentUser,
        string? position = null)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(currentUser.Id))
            headers[CurrentUserHeaderKeys.UserId] = currentUser.Id;
        if (!string.IsNullOrEmpty(currentUser.UserName))
            headers[CurrentUserHeaderKeys.UserName] = currentUser.UserName;
        if (!string.IsNullOrEmpty(currentUser.Name))
            headers[CurrentUserHeaderKeys.Name] = currentUser.Name;
        if (!string.IsNullOrEmpty(currentUser.Surname))
            headers[CurrentUserHeaderKeys.SurName] = currentUser.Surname;
        if (currentUser.Roles is { Length: > 0 })
            headers[CurrentUserHeaderKeys.Role] = string.Join(",", currentUser.Roles);
        if (!string.IsNullOrEmpty(currentUser.ActorUserId))
            headers[CurrentUserHeaderKeys.ActorUserId] = currentUser.ActorUserId;
        if (!string.IsNullOrEmpty(currentUser.ActorUserName))
            headers[CurrentUserHeaderKeys.ActorSub] = currentUser.ActorUserName;
        if (!string.IsNullOrEmpty(currentUser.ConsentId))
            headers[CurrentUserHeaderKeys.ConsentId] = currentUser.ConsentId;
        if (!string.IsNullOrEmpty(position))
            headers[CurrentUserHeaderKeys.Position] = position;
        return headers;
    }

    /// <summary>
    /// Resolves the caller role list used when locally routing a function. Prefers all roles on the
    /// current user; when the user carries no roles, falls back to the roles parsed from the request
    /// <c>role</c> header; returns <c>null</c> when neither is present.
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

        var headerRole = headers is null ? null : GetHeader(headers, CurrentUserHeaderKeys.Role);
        return ParseRolesFromHeader(headerRole);
    }

    /// <summary>
    /// Parses the role header value into an array of role strings.
    /// Supports multiple roles separated by comma or space (e.g. "role1, role2" or "role1 role2").
    /// </summary>
    public static string[]? ParseRolesFromHeader(string? roleHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(roleHeaderValue))
            return null;
        var roles = roleHeaderValue!
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
        return roles.Length == 0 ? null : roles;
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string?> headers, string key)
    {
        return headers.GetValueOrDefault(key);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
