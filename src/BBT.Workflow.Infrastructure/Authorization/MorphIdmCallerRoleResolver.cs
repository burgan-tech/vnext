using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization.Configuration;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Resolves the caller's operation set from morph-idm.
/// <para>
/// One GET to the <c>get-roles</c> function per DI scope, carrying the caller's <c>act_sub</c>,
/// <c>sub</c> and <c>position</c>. The <c>role</c> header is deliberately never sent: with it the
/// endpoint switches to its authorize behaviour and answers yes/no for that one role, whereas the
/// runtime needs the whole set so the existing grant engine can evaluate <c>transition.roles</c>,
/// <c>availableIn</c> narrowing, <c>queryRoles</c> and schema <c>x-roles</c> exactly as it always has.
/// </para>
/// <para>
/// The result — success or failure — is memoized for the scope. No distributed cache is layered on
/// top: the endpoint caches itself, and a second cache here would only add a second place for a stale
/// operation set to hide.
/// </para>
/// </summary>
public sealed class MorphIdmCallerRoleResolver : ICallerRoleResolver
{
    private readonly HttpClient _httpClient;
    private readonly ICurrentUser _currentUser;
    private readonly ICallerPositionAccessor _positionAccessor;
    private readonly MorphIdmOptions _options;
    private readonly ILogger<MorphIdmCallerRoleResolver> _logger;

    /// <summary>
    /// The scope's single in-flight or completed resolution. A <see cref="Lazy{T}"/> over the task
    /// rather than a flag plus a field: several surfaces resolve concurrently on the same scope (the
    /// human-task and subflow reads both fan out), and a flag would let two of them race into two
    /// provider calls.
    /// </summary>
    private readonly Lazy<Task<Result<string[]?>>> _resolution;

    /// <summary>
    /// Headers captured from the first caller, used only as a fallback source of <c>position</c> in
    /// scopes with no ambient HTTP request (background transition execution). Identity itself comes
    /// from <c>ICurrentUser</c>, which is scope-wide and identical at every call site.
    /// </summary>
    private IReadOnlyDictionary<string, string?>? _fallbackHeaders;

    public MorphIdmCallerRoleResolver(
        HttpClient httpClient,
        ICurrentUser currentUser,
        ICallerPositionAccessor positionAccessor,
        IOptions<CallerRoleProviderOptions> options,
        ILogger<MorphIdmCallerRoleResolver> logger)
    {
        _httpClient = httpClient;
        _currentUser = currentUser;
        _positionAccessor = positionAccessor;
        _options = options.Value.MorphIdm;
        _logger = logger;

        // CancellationToken.None on purpose: the memoized task is shared by every surface in the scope,
        // so honouring the first caller's token would let one abandoned read cancel the role set out
        // from under the others. The HttpClient timeout is what bounds this call.
        _resolution = new Lazy<Task<Result<string[]?>>>(
            () => FetchAsync(_fallbackHeaders, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public Task<Result<string[]?>> ResolveRolesAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default)
    {
        if (_resolution.IsValueCreated)
        {
            var memoized = _resolution.Value;
            if (memoized.IsCompletedSuccessfully)
            {
                _logger.CallerRolesServedFromRequestScopeMemo(
                    CallerRoleProviderOptions.MorphIdmProvider, memoized.Result.Value?.Length ?? 0);
            }
            return memoized;
        }

        _fallbackHeaders = headers;
        return _resolution.Value;
    }

    private async Task<Result<string[]?>> FetchAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken)
    {
        var subject = _currentUser.UserName;
        var actor = _currentUser.ActorUserName;
        var position = _positionAccessor.GetPosition() ?? HeaderValue(headers, CurrentUserHeaderKeys.Position);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.GetRolesPath);
            AddHeader(request, CurrentUserHeaderKeys.ActorSub, actor);
            AddHeader(request, CurrentUserHeaderKeys.UserName, subject);
            AddHeader(request, CurrentUserHeaderKeys.Position, position);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            // A known "this caller has no operation set" answer. Empty, not null: falling back to any
            // other source here would silently re-grant what the provider just declined to grant.
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                _logger.CallerRoleProviderReturnedNoContent(
                    CallerRoleProviderOptions.MorphIdmProvider, subject, actor, position);
                return Result<string[]?>.Ok([]);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.CallerRoleProviderCallFailed(
                    null,
                    CallerRoleProviderOptions.MorphIdmProvider,
                    (int)response.StatusCode,
                    response.ReasonPhrase ?? "non-success status");
                return Fail($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.CallerRoleProviderReturnedNoContent(
                    CallerRoleProviderOptions.MorphIdmProvider, subject, actor, position);
                return Result<string[]?>.Ok([]);
            }

            var roles = ParseRoles(body);
            if (roles is null)
            {
                _logger.CallerRoleProviderCallFailed(
                    null, CallerRoleProviderOptions.MorphIdmProvider, (int)response.StatusCode,
                    "response carried no recognizable roles array");
                return Fail("unrecognized response shape");
            }

            _logger.CallerRolesResolvedFromProvider(
                CallerRoleProviderOptions.MorphIdmProvider, roles.Length, stopwatch.ElapsedMilliseconds);
            return Result<string[]?>.Ok(roles);
        }
        catch (Exception ex)
        {
            _logger.CallerRoleProviderCallFailed(
                ex, CallerRoleProviderOptions.MorphIdmProvider, null, ex.Message);
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Reads the roles array from any of the shapes the endpoint is known to answer with:
    /// <c>roles</c>, <c>data.roles</c>, or <c>getRoles.data.roles</c>. Returns null when none is present,
    /// which is treated as a failure rather than as an empty set — an unparseable answer tells us
    /// nothing about the caller, while <c>204</c> and an empty body tell us the set is genuinely empty.
    /// </summary>
    private static string[]? ParseRoles(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return ReadArray(root, "roles")
                   ?? ReadArray(Child(root, "data"), "roles")
                   ?? ReadArray(Child(Child(root, "getRoles"), "data"), "roles");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Child(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } parent
        && parent.TryGetProperty(property, out var child)
            ? child
            : null;

    private static string[]? ReadArray(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } parent)
            return null;
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToArray();
    }

    private static Result<string[]?> Fail(string reason) =>
        Result<string[]?>.Fail(
            WorkflowErrors.CallerRoleResolutionFailed(CallerRoleProviderOptions.MorphIdmProvider, reason));

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string? HeaderValue(IReadOnlyDictionary<string, string?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value) ? value : null;
}
