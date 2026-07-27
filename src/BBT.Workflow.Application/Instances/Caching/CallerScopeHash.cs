using System.Security.Cryptography;
using System.Text;
using BBT.Aether.Users;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Shared caller-scope hashing for the built-in instance function caches. The scope covers
/// role/roles, the current actor identity ($InstanceStarter/$PreviousUser pseudo-roles are
/// matched against ICurrentUser — see TransitionAuthorizationManager, so two callers with
/// identical role headers can receive different responses), the resolved culture (localized
/// labels), requested extensions and the requested version. Folding this hash into cache
/// keys and ETags guarantees a caller switching role, actor, or culture never receives
/// another scope's cached answer or a false 304.
/// </summary>
internal static class CallerScopeHash
{
    /// <summary>
    /// Length of the caller-scope hash (hex chars of the SHA-256 digest).
    /// </summary>
    internal const int Length = 16;

    internal static string Compute(
        ICurrentUser currentUser,
        string? role,
        IReadOnlyList<string>? roles,
        string[]? extensions,
        IReadOnlyDictionary<string, string?>? headers,
        string? version)
    {
        var sortedRoles = roles is { Count: > 0 }
            ? string.Join(',', roles.Order(StringComparer.Ordinal))
            : string.Empty;
        var sortedExtensions = extensions is { Length: > 0 }
            ? string.Join(',', extensions.Order(StringComparer.Ordinal))
            : string.Empty;
        var culture = LanguageResolver.ResolveCulture(headers);

        var callerScope = string.Join('|',
            role ?? string.Empty,
            sortedRoles,
            currentUser.Id ?? string.Empty,
            currentUser.ActorUserName ?? string.Empty,
            culture,
            sortedExtensions,
            version ?? string.Empty);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(callerScope)))[..Length];
    }
}
