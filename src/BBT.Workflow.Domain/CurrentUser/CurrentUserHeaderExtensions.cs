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
    public static Dictionary<string, string?> ToForwardHeaders(this ICurrentUser currentUser)
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
        return headers;
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
        return headers.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
